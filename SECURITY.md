# Security policy

Report vulnerabilities privately (GitHub "Report a vulnerability" on this repo, or email the maintainer).
Please do not open public issues for security problems.

Every build runs the .NET security analyzers as errors, with locked package restores and NuGet vulnerability auditing (see Directory.Build.props). RfScout treats all received RF data, capture files and decoder output as untrusted input.
The threat model lives in docs/threat-model.md.
