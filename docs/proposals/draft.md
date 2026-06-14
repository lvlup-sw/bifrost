`System.Collections.Concurrent` has thread-safe collections for a variety of workloads, but no priority-ordered collection. `PriorityQueue<TElement, TPriority>` was deliberately designed as not thread-safe (#43957), which leaves concurrent scenarios unsupported. 

In fact, no mainstream runtime ships a _scalable_ concurrent priority queue today. Java offers `PriorityBlockingQueue`, but this is a blocking implementation (global lock). C++ offers .. skiplist .. degrades at ~8 threads like all SkipLists do..

.. Past decade there has been extensive research into implementations with _relaxed semantics_, where strict correctness is traded for improved performance/scalability.

### Demonstrated Need
Community/ecosystem demand for this data structure spans over a decade:

| Issue   | Year | Request                                                      | Status      |
| ------- | ---- | ------------------------------------------------------------ | ----------- |
| #13903  | 2014 | Add priority queue (thread safety requested in comments)     | Closed      |
| #32700  | 2020 | IProducerConsumerCollection injection in Channels for priority support | Future      |
| #43957  | 2020 | PriorityQueue — explicitly rejected thread safety            | Implemented |
| #52205  | 2021 | Concurrent/multi-threaded version of PriorityQueue<T>        | Closed      |
| #62761  | 2021 | Priority Channels with async APIs for TTL-based ordering     | Future      |
| #101292 | 2024 | Channel.CreateBoundedPrioritized                             | Future      |

Common use cases include priority channels / QoS pipelines (#32700, #101292), priority task schedulers, A* pathfinding/game AI, amongst others.

### Acceptance Criteria
@stephentoub stated the bar for new concurrent collections in [this comment](https://github.com/dotnet/runtime/issues/52205#issuecomment-833964252):
> Our policy is to only add a Concurrent collection when a) there is significant demonstrated need, and b) the implementation can be made faster and more scalable than just taking a lock around every operation.

This proposal targets (b) by implementing a **MultiQueue**: an array of `n ≈ 4 × ProcessorCount` sub-queues, each an ordinary sequential arity-4 min-heap behind its own lock. ..Add citations for paper

.. Theoretical characteristics (relaxed semantics, scalability)
.. Comparison to previous SoTA
.. For more detailed information, see my write-up here
.. Benchmarks of a prototype implementation are available here