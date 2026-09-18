# Use trusted NuGet publishing credentials

High confidence for stored-key usage; remote key lifetime/permissions are unknown. publish.yml:56 and :65 reference secrets.NUGET_API_KEY; :57/:66 target nuget.org. This is not a private-feed exception. Microsoft Learn trusted publishing documents GitHub OIDC issuance; workflow login output may also be named NUGET_API_KEY, so G4 checks provenance, not the token label. The current code proves stored-secret usage, not a leak.

Investigation: local workflow read; nuget.org account trust configuration is not present in this repository and was not accessed. This affects rollout readiness, not the observed legacy credential source.

## Open questions

Which NuGet account owner will create the workflow trust policy? (needs human input; repository workflow inspected, account policy is external.)

### Investigation Log

#### Evidence and competing explanation (2026-09-17)

Read the cited production path and existing guards at revision 3a17ed0f45dfd64d953712ccda286b0915bef838. Catalog applicability is net10.0 / C# 14; no preview feature required. Investigation remains bounded as described above.
