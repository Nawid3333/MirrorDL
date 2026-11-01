# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Nawid Salehie
"""Test mirror: serves one file with HTTP Range support, optionally slow, flaky or limited."""
import argparse, os, random, re, sys, threading, time
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler

p = argparse.ArgumentParser()
p.add_argument("--port", type=int, required=True)
p.add_argument("--file", required=True)
p.add_argument("--rate", type=float, default=0, help="bytes/s per connection (0 = unlimited)")
p.add_argument("--total-rate", type=float, default=0, help="bytes/s for all connections together (0 = unlimited)")
p.add_argument("--fail", type=float, default=0, help="chance per 64 KiB to drop the connection")
p.add_argument("--max-conns", type=int, default=0, help="answer 503 above this many connections")
p.add_argument("--limit-status", type=int, default=503, help="status sent above --max-conns")
p.add_argument("--no-range", action="store_true")
p.add_argument("--name", help="Content-Disposition file name")
p.add_argument("--latency", type=float, default=0, help="seconds before each answer, as a far server's round trip")
p.add_argument("--burst", type=int, default=0, help="bytes each connection sends at full speed before --rate applies")
p.add_argument("--flaky-503", type=float, default=0, help="chance per request to answer 503, as a briefly overloaded server")
p.add_argument("--log", help="file to append each answer's status to (one per line), to count them")
p.add_argument("--pause-every", type=int, default=0, help="bytes a connection sends before each --pause")
p.add_argument("--pause", type=float, default=0, help="seconds a connection freezes after every --pause-every bytes")
a = p.parse_args()
size = os.path.getsize(a.file)
active = 0
lock = threading.Lock()
line_lock = threading.Lock()
line_free = 0.0  # when the line all connections share is free again (--total-rate)
log_lock = threading.Lock()


def log(status):
    if a.log:
        with log_lock, open(a.log, "a") as f:
            f.write(f"{status}\n")


def take_line(count):
    """Waits for the turn of count bytes on the line all connections share."""
    global line_free
    with line_lock:
        start = max(time.time(), line_free)
        line_free = start + count / a.total_rate
    delay = start - time.time()
    if delay > 0:
        time.sleep(delay)


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):
        pass

    def do_GET(self):
        global active
        if a.latency:
            time.sleep(a.latency)
        refuse = 0
        with lock:
            if a.max_conns and active >= a.max_conns:
                refuse = a.limit_status
            elif a.flaky_503 and random.random() < a.flaky_503:
                refuse = 503
            else:
                active += 1
        if refuse:
            log(refuse)
            self.send_response(refuse)
            self.send_header("Content-Length", "0")
            self.end_headers()
            return
        try:
            self.serve()
        except (ConnectionError, BrokenPipeError):
            pass
        finally:
            with lock:
                active -= 1

    def serve(self):
        start, end, partial = 0, size - 1, False
        header = self.headers.get("Range")
        if header and not a.no_range:
            m = re.match(r"bytes=(\d+)-(\d*)$", header)
            start = int(m.group(1))
            end = min(int(m.group(2)) if m.group(2) else size - 1, size - 1)
            partial = True
            if start >= size:
                # As real servers do, also for an empty file: there is no such range.
                self.send_response(416)
                self.send_header("Content-Range", f"bytes */{size}")
                self.send_header("Content-Length", "0")
                self.end_headers()
                return
        log(206 if partial else 200)
        self.send_response(206 if partial else 200)
        self.send_header("Content-Length", str(end - start + 1))
        self.send_header("Content-Type", "application/octet-stream")
        if partial:
            self.send_header("Content-Range", f"bytes {start}-{end}/{size}")
        if not a.no_range:
            self.send_header("Accept-Ranges", "bytes")
        if a.name:
            self.send_header("Content-Disposition", f'attachment; filename="{a.name}"')
        self.end_headers()
        with open(a.file, "rb") as f:
            f.seek(start)
            left, sent, t0 = end - start + 1, 0, time.time()
            while left > 0:
                chunk = f.read(min(65536, left))
                if a.fail and random.random() < a.fail:
                    self.close_connection = True
                    return
                if a.total_rate:
                    take_line(len(chunk))
                self.wfile.write(chunk)
                left -= len(chunk)
                sent += len(chunk)
                if a.pause_every and sent // a.pause_every != (sent - len(chunk)) // a.pause_every:
                    time.sleep(a.pause)
                    if t0 is not None:
                        t0 += a.pause  # the freeze doesn't count against --rate
                if a.rate and sent > a.burst:
                    if a.burst and sent - len(chunk) <= a.burst:
                        t0 = time.time() - a.burst / a.rate  # the throttle starts now, after the burst
                    ahead = sent / a.rate - (time.time() - t0)
                    if ahead > 0:
                        time.sleep(ahead)


class Server(ThreadingHTTPServer):
    daemon_threads = True

    def handle_error(self, request, client_address):
        # A client closing a connection early is normal: mirrordl drops a range once another
        # connection has taken over the rest of it.
        if isinstance(sys.exc_info()[1], ConnectionError):
            return
        super().handle_error(request, client_address)


Server(("127.0.0.1", a.port), Handler).serve_forever()
