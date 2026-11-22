# JsonPit open design questions

- Which package should own copying media and documents into the synchronized tree, naming them, and resolving references from pit items?
- How should history pruning identify media references that every replica has dropped before collecting orphaned files?
- What synchronization guarantees should we document for offline replicas and cloud providers that download content on demand?
- Is a continuously running service needed for unattended maintenance, and should it ship as a Docker image?

The [requirements](Requirements.md) record the original design goals. The [test suite](JsonPit.Tests/) lives in this repository. The [RAIkeep solution](https://github.com/Burkhardt/RAIkeep/blob/main/RAIkeep.slnx) builds the components together.
