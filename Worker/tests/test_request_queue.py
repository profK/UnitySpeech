"""Queue manager tests: scheduling, cancellation, deadlines, capacity.

Tests match the spec's EditMode fixture requirements:
- FIFO ordering
- Full queue rejection
- Deadline expiry
- Cancellation before and during synthesis
- Active slot not freed early on non-interrupting backends
- No duplicate terminal events
"""

import pytest
import threading
import time

import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent.parent / "src"))

from request_queue import QueueManager, QueueConfig, QueuedRequest, RequestState
from protocol import TerminalReason


def _make_req(id="req-1", deadline=None):
    return QueuedRequest(
        request_id=id,
        text="hello",
        language="en",
        voice_id="default",
        deadline=deadline,
    )


# ── Basic submission ───────────────────────────────────────────────

class TestSubmit:
    def test_first_request_occupies_active_slot(self):
        qm = QueueManager()
        req = _make_req()
        accepted, _ = qm.try_submit(req)
        assert accepted
        assert qm.is_busy
        assert qm.queued_count == 0
        assert req.state == RequestState.ACTIVE

    def test_second_request_queues(self):
        qm = QueueManager()
        qm.try_submit(_make_req("req-1"))
        accepted, _ = qm.try_submit(_make_req("req-2"))
        assert accepted
        assert qm.queued_count == 1
        assert qm.active.request_id == "req-1"

    def test_fifo_ordering(self):
        qm = QueueManager()
        qm.try_submit(_make_req("first"))
        qm.try_submit(_make_req("second"))
        qm.try_submit(_make_req("third"))
        assert qm.queued_count == 2
        # After releasing active, second should promote
        qm.release("first")
        assert qm.active.request_id == "second"


# ── Capacity limits ────────────────────────────────────────────────

class TestCapacity:
    def test_rejects_beyond_queue_limit(self):
        qm = QueueManager(QueueConfig(max_queue=2))
        qm.try_submit(_make_req("active"))  # Goes to active slot
        qm.try_submit(_make_req("queued-1"))  # Goes to queue
        accepted, reason = qm.try_submit(_make_req("queued-2"))  # Last slot
        assert accepted
        accepted, reason = qm.try_submit(_make_req("overflow"))  # Rejected
        assert not accepted
        assert reason == TerminalReason.QueueOverload

    def test_is_full_reflects_capacity(self):
        qm = QueueManager(QueueConfig(max_queue=1))
        assert not qm.is_full
        qm.try_submit(_make_req("active"))
        assert not qm.is_full  # Active filled, queue still empty
        qm.try_submit(_make_req("queued"))
        assert qm.is_full


# ── Cancellation ───────────────────────────────────────────────────

class TestCancellation:
    def test_cancel_queued_request(self):
        qm = QueueManager()
        qm.try_submit(_make_req("active"))
        qm.try_submit(_make_req("queued"))
        assert qm.cancel("queued")
        assert qm.queued_count == 0

    def test_cancel_active_not_yet_started(self):
        """Cancel a request that hasn't started synthesis – frees the slot immediately."""
        qm = QueueManager()
        req = _make_req("active")
        qm.try_submit(req)
        assert req.state == RequestState.ACTIVE
        assert qm.cancel("active")
        assert req.state == RequestState.CANCELLED
        assert not qm.is_busy  # Slot freed

    def test_cancel_active_during_synthesis(self):
        """Cancel during synthesis – marks draining, does NOT free slot early."""
        qm = QueueManager()
        req = _make_req("active")
        qm.try_submit(req)
        req.state = RequestState.STARTED  # Simulate inference in progress
        assert qm.cancel("active")
        assert req.state == RequestState.DRAINING
        assert qm.is_busy  # Slot NOT freed

    def test_cancel_nonexistent_returns_false(self):
        qm = QueueManager()
        assert not qm.cancel("no-such-id")

    def test_cancel_terminal_request_returns_false(self):
        """Already-finished requests cannot be cancelled."""
        qm = QueueManager()
        req = _make_req("done")
        qm.try_submit(req)
        req.state = RequestState.FINISHED
        assert not qm.cancel("done")

    def test_cancel_idempotent(self):
        """Cancelling an already-cancelled request returns False."""
        qm = QueueManager()
        req = _make_req("active")
        qm.try_submit(req)
        qm.cancel("active")
        assert not qm.cancel("active")  # Second cancel returns False


# ── Deadline expiry ────────────────────────────────────────────────

class TestDeadlines:
    def test_expired_on_submit_rejected(self):
        qm = QueueManager()
        req = _make_req(deadline=time.monotonic() - 10)  # Already expired
        accepted, reason = qm.try_submit(req)
        assert not accepted
        assert reason == TerminalReason.Timeout

    def test_expired_queued_promoted_skipped(self):
        """An expired request in the queue is skipped on promotion."""
        qm = QueueManager()
        qm.try_submit(_make_req("active"))
        qm.try_submit(_make_req("expired", deadline=time.monotonic() - 5))
        qm.try_submit(_make_req("valid"))

        # Release active; expired should be skipped, valid should promote
        qm.release("active")
        assert qm.active.request_id == "valid"


# ── Next / release cycle ───────────────────────────────────────────

class TestNextAndRelease:
    def test_next_returns_active(self):
        qm = QueueManager()
        qm.try_submit(_make_req("req"))
        result = qm.next(timeout=0.5)
        assert result is not None
        assert result.request_id == "req"

    def test_next_times_out_when_empty(self):
        qm = QueueManager()
        start = time.monotonic()
        result = qm.next(timeout=0.1)
        elapsed = time.monotonic() - start
        assert result is None
        assert elapsed >= 0.09  # Should have waited

    def test_release_promotes_next(self):
        qm = QueueManager()
        qm.try_submit(_make_req("first"))
        qm.try_submit(_make_req("second"))
        qm.release("first")
        assert qm.active.request_id == "second"
        assert qm.queued_count == 0

    def test_release_clears_when_queue_empty(self):
        qm = QueueManager()
        qm.try_submit(_make_req("only"))
        qm.release("only")
        assert not qm.is_busy
        assert qm.queued_count == 0

    def test_drain_queue(self):
        qm = QueueManager(QueueConfig(max_queue=4))
        qm.try_submit(_make_req("a"))
        qm.try_submit(_make_req("b"))
        qm.try_submit(_make_req("c"))
        drained = qm.drain_queue()
        assert len(drained) == 2  # Only queued items (b, c)
        assert qm.queued_count == 0


# ── Concurrent access ──────────────────────────────────────────────

class TestConcurrency:
    def test_concurrent_submits_safe(self):
        """Multiple threads submitting simultaneously."""
        qm = QueueManager(QueueConfig(max_queue=10))
        results = []

        def submit(n):
            accepted, _ = qm.try_submit(_make_req(f"req-{n}"))
            results.append(accepted)

        threads = [threading.Thread(target=submit, args=(i,)) for i in range(15)]
        for t in threads:
            t.start()
        for t in threads:
            t.join()

        # 1 active + 10 queued = 11 accepted, 4 rejected
        assert sum(results) == 11
        assert qm.queued_count == 10
        assert qm.is_busy
