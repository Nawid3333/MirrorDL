# Speed: what was measured, what helped, what didn't

MirrorDL's speed decisions are made by measuring, not by guessing: an idea that looks right can make
downloads slower. This page has the measurements behind the current behavior, including the ideas
that were tried and dropped, so that nobody has to try them again blind.

## How to measure

```
pwsh tools/speed-test.ps1 -Exe MirrorDL/bin/Release/net10.0/win-x64/mirrordl.exe [-Scenario normal,burst] [-Runs 5]
```

It starts local test mirrors (`tools/testserver.py`) on this PC, downloads each scenario several
times with the real program and prints the median time (with min, max, MB/s, failed runs and the
number of 429/503 answers). Every run is checked by its SHA-256. Nothing goes over the internet line,
so the results show MirrorDL's own behavior, not the speed of the connection.

Runs vary: two runs of the same build on `normal` gave medians of 4.88 s and 4.89 s, single runs
between 4.8 s and 5.4 s (the engine works in half-second steps). Differences under about 0.1 s
in a median are noise. Scenarios with random errors (`flaky503`, `flakyneighbor`) need 10 runs.

| Scenario | What it is like |
|---|---|
| `normal` | two good links, more connections help |
| `limit503` | a host that allows 4 connections at once and answers 503 to more |
| `flaky503` | a host that answers 1 request in 4 with 503 (briefly overloaded) |
| `burst` | a link sending 2 MB fast on each connection, then 64 KB/s, next to a steady one |
| `slowlink` | a slow link (100 KB/s per connection) next to a fast one, from the start |
| `stall` | a link whose connections freeze for 2.5 s after every 3 MB, next to a steady one |
| `flakyneighbor` | a good link next to one that drops connections often |
| `latency` | two far links: 200 ms before each answer |
| `oneconn` | a far host that allows one connection only |
| `maxspeed` | two local links without any limit, 1.5 GB: MirrorDL's own top speed |
| `maxspeed-curl` | the same file with curl over one connection: what the test server delivers |

## What was measured (November 2026)

Medians in seconds, 5 runs (10 where marked), Release build, on the development PC.

| Scenario | Before | After | |
|---|---|---|---|
| `limit503` | 47.0 | 26.5 | idea 1 |
| `flaky503` (10 runs) | 15.3 | 12.8 | ideas 1 and 5 |
| `burst` | 26.6 | 6.3 | idea 2 |
| `slowlink` | 8.26 | 6.92 | idea 4 |
| `latency` | 1.70 | 1.07 | idea 4 |
| `normal` | 4.88 | 4.81 | guard |
| `stall` | 4.83 | 4.79 | guard |
| `oneconn` | 0.77 | 0.75 | guard |
| `flakyneighbor` (10 runs) | 12.0 | 11.4 | guard |
| `maxspeed` | 797 MB/s | 854 MB/s | |
| `maxspeed-curl` | 520-580 MB/s | | |

"After" is the final code, measured as a whole (not the single ideas): no scenario got slower.

### 1. A busy answer (429, 503) doesn't cost a connection - adopted

Before, a connection that got 429 or 503 while the link's other connections worked ended, and the
link was allowed one connection less, since many hosts answer 503 to "too many connections". The
Tuner then rested the link for 30 s or more before trying more connections again.

Against a host allowing 4 connections, that left the download at about half the possible speed
(47.0 s, the ideal being 24 s): refused connections lowered the cap below what the host allowed.
Now such a connection waits a second or two and asks again, without counting it as an error:
26.5 s. Still "busy" after 8 tries in a row, the host doesn't want that many connections, and the
connection leaves as before - so a host that refuses for good isn't asked forever. A gentler variant with growing pauses (2, 4, 8 ... s, counted as errors) reached 27.5 s with
fewer refused requests (40 instead of 60 in the whole download), and 14.4 s instead of 12.8 s on
`flaky503`. The faster one was kept: about two refused requests a second is still gentler than
aria2, which by default asks again at once. Other refusals (403 and the like) still end the
connection as before.

### 2. At the end, a slow part is judged by its speed of the last seconds - adopted

At the end of a download, a connection without work takes over the rest of one that would still take
long. Its speed was its average since its part started. A link that starts fast and then slows to a
trickle - some hosts send the first megabytes of each connection at full speed and throttle after -
kept a high average for long, so its rest wasn't taken over: 26.6 s. Now the lower of the average and
the speed over the last one to two seconds counts: 6.3 s. A link that is slow from the start (8.26 s)
and one that freezes for 2.5 s now and then (`stall`, 4.83 s) are unchanged: a short freeze alone
doesn't make a part look slow for long enough (a part is only judged after 4 s, and must still need
more than 5 s at that speed).

### 3. No rest for the Tuner after a measurement that couldn't be made - dropped

When the Tuner tries more connections for a link, it measures whether the download got faster. If
connections come and go meanwhile (another link dropping them), the measurement is spoiled; after
three tries it gives up, goes back to the connections it had, and rests the link (30 s, doubling).
The idea: don't rest then, try again soon. Measured on `flakyneighbor`: 11.5 -> 12.6 s, and a variant
that rests only when the host refused connections during the try: 12.0 -> 12.5 s. Both slower:
trying again soon brings more connections that come and go, which spoils more measurements and costs
more than it gains. The rest stays.

### 4. Content samples four at a time - adopted

Before a download, MirrorDL compares 64 KB samples of every link (4 for a new download, up to about
20 when unfinished files are put together). They were fetched one after another, so a far or slow
link made the start wait: `latency` 1.70 s. Four at a time: 1.07 s (two at a time: 1.28 s). The
slow link in `slowlink` no longer holds the start up either: 8.26 -> 6.92 s. A host that refuses
several requests at once gets the missing samples one at a time; `oneconn` is unchanged (0.75 s).

### 5. A busy answer while a link is checked is asked again - adopted (found while measuring)

The first request to a link (for its size) and the samples had no second chance: one 429 or 503 at
that moment left the link out of the whole download - and with a single link, the download failed.
Now such an answer is asked again three times, after 1, 2 and 4 s. With a host refusing 1 request
in 4 at random (far more than real hosts do), a single link failed to start in 1 of 15 runs before;
since, only when all four answers were refusals: 1 run in 80, after 7 s of trying. Two tries
(after 1 and 2 s) weren't enough there: 3 runs in 75 still failed.

## Top speed

On the development PC, MirrorDL downloads 1.5 GB from two local links at about 800-860 MB/s (about
7 Gbit/s); curl gets 520-580 MB/s from the same test server over one connection. So MirrorDL itself is
far from being the limit on any internet line - also on gigabit fiber, about 120 MB/s. On a slow
line, the line is the limit, not MirrorDL (for Wi-Fi, check the link speed Windows shows).
