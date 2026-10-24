# Contributing

This is an experimental Unity/Python prototype, not a production networking
framework. Small, reproducible fixes and documentation improvements are welcome.

## Before Opening a Change

- Read the current architecture and protocol documents, not an archived snapshot.
- Include Python/OS versions and, for scene issues, the Unity version and setup.
- Describe the expected behavior, observed behavior and minimal reproduction.
- Keep credentials, private endpoints, runtime captures and bytecode out of Git.
- Do not modify historical snapshots to make them look like current releases.

Run the bounded loopback checks listed in the root README. A protocol change
needs corresponding Python and C# updates and compatible serialization tests;
the existing Python checks alone cannot certify Unity integration.

Use loopback or a trusted isolated network. Do not test by exposing worker ports
to the public Internet. Source contributions are covered by the repository's
MIT license; third-party dependencies retain their own terms.
