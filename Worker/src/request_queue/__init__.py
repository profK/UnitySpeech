"""Request queue: one active, N queued, FIFO, deadline-aware, cancellable."""

from __future__ import annotations

import logging
import threading
import time
from dataclasses import dataclass, field
from enum import Enum
from typing import Callable, Dict, List, Optional, Tuple

from protocol import TerminalReason

log = logging.getLogger("queue")


class RequestState(Enum):
    QUEUED = "queued"
    ACTIVE = "active"
    CANCELLED = "cancelled"
    STARTED  = "started"    # synthesis in progress, may not interrupt
    DRAINING = "draining"   # cancellation requested during synthesis
    FINISHED = "finished"
    FAILED   = "failed"
    EXPIRED  = "expired"


@dataclass
class QueuedRequest:
    request_id: str
    text: str
    language: str
    voice_id: str
    deadline: Optional[float]  # monotonic timestamp, None = no deadline
    enqueued_at: float = field(default_factory=lambda: time.monotonic())
    state: RequestState = RequestState.QUEUED
    active_at: Optional[float] = None
    cancelled: bool = False
    response_callback: Optional[Callable] = None


@dataclass
class QueueConfig:
    max_active: int = 1
    max_queue: int = 8
    poll_interval: float = 0.05  # 50 ms


# ── Queue Manager ───────────────────────────────────────────────────

class QueueManager:
    """Manages incoming requests: queue, scheduling, deadlines, cancellation.

    One active request at a time (configurable). Additional requests queue
    FIFO. Requests beyond the queue limit are rejected immediately.

    Thread-safe: safe to call from the HTTP handler and the worker thread.
    """

    def __init__(self, config: Optional[QueueConfig] = None):
        self.config = config or QueueConfig()
        self._lock = threading.Lock()
        self._queue: List[QueuedRequest] = []
        self._active: Optional[QueuedRequest] = None
        self._pending_cancel: set = set()  # request IDs pending cancel
        self._not_empty = threading.Event()

    @property
    def active(self) -> Optional[QueuedRequest]:
        with self._lock:
            return self._active

    @property
    def queued_count(self) -> int:
        with self._lock:
            return len(self._queue)

    @property
    def is_busy(self) -> bool:
        with self._lock:
            return self._active is not None

    @property
    def is_full(self) -> bool:
        """True when the queue is at capacity. Note: one more request may
        still be accepted if the active slot is free (try_submit handles this)."""
        with self._lock:
            return len(self._queue) >= self.config.max_queue

    def try_submit(self, request: QueuedRequest) -> Tuple[bool, Optional[TerminalReason]]:
        """Submit a request. Returns (accepted, reject_reason)."""
        with self._lock:
            if request.deadline is not None and request.deadline <= time.monotonic():
                return False, TerminalReason.Timeout
            if self._active is None:
                self._active = request
                request.state = RequestState.ACTIVE
                request.active_at = time.monotonic()
                self._not_empty.set()
                return True, None
            if len(self._queue) >= self.config.max_queue:
                log.warning("Queue full, rejecting %s", request.request_id)
                return False, TerminalReason.QueueOverload
            self._queue.append(request)
            self._not_empty.set()
            return True, None

    def cancel(self, request_id: str) -> bool:
        """Request cancellation. Returns True if the request was found."""
        with self._lock:
            if self._active and self._active.request_id == request_id:
                if self._active.state in (RequestState.FINISHED, RequestState.FAILED,
                                           RequestState.EXPIRED, RequestState.CANCELLED):
                    return False  # already terminal
                self._active.cancelled = True
                if self._active.state == RequestState.ACTIVE:
                    # Not yet started synthesis – free slot immediately
                    self._active.state = RequestState.CANCELLED
                    self._active = None
                elif self._active.state in (RequestState.STARTED, RequestState.DRAINING):
                    # Synthesis in progress – mark for drain after current chunk
                    self._active.state = RequestState.DRAINING
                    self._pending_cancel.add(request_id)
                return True
            # Search and remove from queue
            for i, req in enumerate(self._queue):
                if req.request_id == request_id:
                    req.cancelled = True
                    req.state = RequestState.CANCELLED
                    self._pending_cancel.add(request_id)
                    self._queue.pop(i)
                    return True
            return False

    def next(self, timeout: float = 0.5) -> Optional[QueuedRequest]:
        """Block until a request is available to process, or timeout.

        Returns the active request when one is set and the event is signalled.
        If the active slot is empty (already consumed), promotes from the queue.
        """
        if not self._not_empty.wait(timeout=timeout):
            return None

        with self._lock:
            self._not_empty.clear()
            # Return the active request (may have been set by try_submit or release)
            if self._active is not None:
                return self._active
            # Active was already consumed; promote from queue
            while self._queue:
                req = self._queue.pop(0)
                if req.cancelled:
                    log.info("Skipping cancelled queued request %s", req.request_id)
                    continue
                if req.deadline is not None and req.deadline <= time.monotonic():
                    req.state = RequestState.EXPIRED
                    continue
                self._active = req
                req.state = RequestState.ACTIVE
                req.active_at = time.monotonic()
                return self._active
            return None

    def release(self, request_id: str) -> None:
        """Mark the active slot as released. Promotes next queued request."""
        with self._lock:
            if self._active and self._active.request_id == request_id:
                self._active = None
            # Try to promote next queued request
            while self._queue:
                req = self._queue.pop(0)
                if req.cancelled:
                    continue
                if req.deadline is not None and req.deadline <= time.monotonic():
                    req.state = RequestState.EXPIRED
                    continue
                self._active = req
                req.state = RequestState.ACTIVE
                req.active_at = time.monotonic()
                self._not_empty.set()
                break

    def drain_queue(self) -> List[QueuedRequest]:
        """Return and clear all queued requests (for shutdown)."""
        with self._lock:
            cancelled = list(self._queue)
            self._queue.clear()
            self._not_empty.clear()
            return cancelled
