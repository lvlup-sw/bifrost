# Arithmetic boundaries

Eight regression cases failed before the changes; all nine checks passed after.

- Priority window conversion used floating-point seconds before truncation. At
  frequency 10,000,000, a 21-tick window became 20 timestamp units. Conversion now
  uses an exact `Int128` product/division once during queue construction.
- Oversized windows silently converted outside the signed timestamp range. They
  now fail configuration with `ArgumentOutOfRangeException`.
- Signed key subtraction wrapped at extreme timestamps and reversed urgency.
  Keys now saturate at the signed limits; saturated keys can tie. The per-item
  path remains integer-only and allocation-free.
- Interval scheduling threw at calendar exhaustion, including a valid interval
  larger than the remaining date range. It now returns null when no representable
  next occurrence exists, checking division before multiplying the step count.
- Jitter near the calendar boundary could throw even when the base occurrence was
  valid. An unrepresentable jitter draw now falls back to the valid base occurrence,
  matching the existing fallback for draws that would fall at/before now.

Calendar checks account for both local and UTC ticks. Fractional conversion tests
cover three timestamp frequencies and 1,000 window sizes each. Boundary jitter
uses 1,000 draws; it is randomized coverage, not exhaustive RNG exploration.
