# Reject uninspectable guard subjects

Source-confirmed scanner behavior; no claim current CI is inspecting native files. BannedTimeApiTests.cs:143–146 and :208–211, AotSafetyTests.cs:164–167 all continue on missing metadata. Their callers assert violations.IsEmpty. Fixed current assembly paths reduce likelihood; this obligation concerns guard integrity, not a reported application defect. Existing BannedApiCheck_CatchesDeliberateViolation validates a DateTime.UtcNow reference in the test assembly; it does not exercise the native-subject branch or ensure exact assembly identity.

G3 kill fixture: copy an existing compiled DLL to a scratch directory and clear the PE CLI-header data directory, retaining a valid native PE container; verify !HasMetadata, then require scanner failure. Missing file currently throws and is not a finding. The fixture was subsequently executed: scanner-probe.txt records HasMetadata=False and zero violations from all three existing scanner methods. Source in evidence/scanner-probe/Program.cs.txt; product binaries were not changed.

## Open questions

None

### Investigation Log

#### Evidence and competing explanation (2026-09-17)

Read the cited production path and existing guards at revision 3a17ed0f45dfd64d953712ccda286b0915bef838. Catalog applicability is net10.0 / C# 14; no preview feature required. No unresolved implementation question remains for the narrow source-backed claim.
