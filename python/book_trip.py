"""BookTrip in Python: the same flight -> hotel -> car workflow on the same scheduler emulator.

    python book_trip.py worker          run a worker (Ctrl+C to stop)
    python book_trip.py start <id>      book a trip
    python book_trip.py wait <id>       wait for the trip and print the result
    python book_trip.py ledger          what the fake provider booked

Run the worker with PRAGUE_BOOKING_CRASH=before-car to make it die right before the car booking,
then start a new worker: the trip resumes from its history, like the C# demo.
"""
import json
import os
import sys
import threading
from pathlib import Path

from durabletask import task
from durabletask.azuremanaged.client import DurableTaskSchedulerClient
from durabletask.azuremanaged.worker import DurableTaskSchedulerWorker

ENDPOINT = os.getenv("PRAGUE_DTS_ENDPOINT", "http://localhost:18080")
LEDGER = Path(os.getenv("PRAGUE_BOOKING_DATA", "data/python-booking"))


def book(service: str, operation_id: str) -> dict:
    """Fake provider: one booking per operation ID, like a real idempotency key."""
    LEDGER.mkdir(parents=True, exist_ok=True)
    path = LEDGER / f"booking-{operation_id.replace('/', '_')}.json"
    if path.exists():
        return json.loads(path.read_text())
    receipt = {"operation_id": operation_id, "service": service, "booked_by_pid": os.getpid()}
    path.write_text(json.dumps(receipt))
    return receipt


def book_flight(ctx: task.ActivityContext, operation_id: str) -> dict:
    return book("flight", operation_id)


def book_hotel(ctx: task.ActivityContext, operation_id: str) -> dict:
    return book("hotel", operation_id)


def book_car(ctx: task.ActivityContext, operation_id: str) -> dict:
    marker = LEDGER / f"crash-{operation_id.replace('/', '_')}"
    if os.getenv("PRAGUE_BOOKING_CRASH") == "before-car" and not marker.exists():
        LEDGER.mkdir(parents=True, exist_ok=True)
        marker.touch()
        print(f"CONTROLLED CRASH: before-car; owned PID={os.getpid()}", flush=True)
        os._exit(1)
    return book("car", operation_id)


def book_trip(ctx: task.OrchestrationContext, trip: dict):
    flight = yield ctx.call_activity(
        book_flight, input=f"{ctx.instance_id}/flight")
    hotel = yield ctx.call_activity(
        book_hotel, input=f"{ctx.instance_id}/hotel")
    car = yield ctx.call_activity(
        book_car, input=f"{ctx.instance_id}/car")
    return {"trip": trip, "bookings": [flight, hotel, car]}


def connect_client() -> DurableTaskSchedulerClient:
    return DurableTaskSchedulerClient(
        host_address=ENDPOINT, secure_channel=False, taskhub="default", token_credential=None)


def run_worker() -> None:
    with DurableTaskSchedulerWorker(
            host_address=ENDPOINT, secure_channel=False, taskhub="default", token_credential=None) as worker:
        worker.add_orchestrator(book_trip)
        worker.add_activity(book_flight)
        worker.add_activity(book_hotel)
        worker.add_activity(book_car)
        worker.start()
        print(f"python worker {os.getpid()} connected to {ENDPOINT}", flush=True)
        threading.Event().wait()


def main(args: list[str]) -> int:
    if not ENDPOINT.startswith(("http://localhost:", "http://127.0.0.1:")):
        print("This sample only talks to the local emulator.", file=sys.stderr)
        return 2
    command = args[0] if args else ""
    if command == "worker":
        run_worker()
        return 0
    if command == "ledger":
        receipts = [json.loads(p.read_text()) for p in sorted(LEDGER.glob("booking-*.json"))]
        counts = {s: sum(r["service"] == s for r in receipts) for s in ("flight", "hotel", "car")}
        print(json.dumps({"counts": counts, "receipts": receipts}, indent=2))
        return 0
    if command in ("start", "wait") and len(args) == 2:
        client = connect_client()
        if command == "start":
            trip = {"from": "SFO", "to": "Seattle"}
            print(client.schedule_new_orchestration(book_trip, input=trip, instance_id=args[1]))
            return 0
        state = client.wait_for_orchestration_completion(args[1], timeout=120)
        if state is None:
            print("not finished within 120 s", file=sys.stderr)
            return 1
        print(state.runtime_status.name)
        print(state.serialized_output)
        return 0 if state.runtime_status.name == "COMPLETED" else 1
    print(__doc__, file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
