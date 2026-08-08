# Static Initial Password

- **Status:** Doing
- **Issue:** [#1273](https://github.com/TetronIO/JIM/issues/1273)
- **Follows:** [#1121](https://github.com/TetronIO/JIM/issues/1121) Initial password generation and delivery on provisioning
- **Superseded eventually by:** [#1252](https://github.com/TetronIO/JIM/issues/1252) Deliver a new account's initial password by email (sub-task of [#618](https://github.com/TetronIO/JIM/issues/618))
- **UI mockup:** [Initial Password: adding a static option](https://claude.ai/code/artifact/6e7bbeb5-2023-47b8-beed-0d4f9828b4d4)

## Overview

Let an administrator choose one password that JIM sets on every account a Synchronisation Rule provisions, so a new starter can be told what it is. Deliberately not recommended, and the portal says so beside the option.

#1121 generates a different password per account and stores none of them, which means nobody can tell a new starter what theirs is. That is right for the password's actual job (getting the account into a state the directory will accept and enable) and wrong for the day-one handover, which currently costs an administrator one manual Set Password per person. #1252 is the proper answer; it does not exist yet, and our own testing needs a known password now.

## The one thing this changes about JIM

**JIM stores a password for the first time.** Everything else here is ordinary work; this is the part that needs deciding rather than implementing.

The security rule in `docs/concepts/passwords.md` currently reads: *no password value is ever stored*. After this it reads: never stored, **except a static initial password an administrator chose deliberately**. That amendment ships in this change, not after it, so the documentation never asserts something the code has stopped honouring. `engineering/DEVELOPER_GUIDE.md` > *3b. Password Channel* gets the same correction.

Handling matches a Connected System's bind credential, which is the existing precedent for a secret JIM must keep:

| Concern | Treatment |
|---|---|
| At rest | Encrypted via `ICredentialProtectionService.Protect` |
| Portal | Write-only: a masked field that is blank unless a new value is being set |
| REST | Write-only field on the update DTO; absent from every response |
| PowerShell | `-StaticPassword <securestring>` in; `Get-` reports only that one is set, and when |
| Configuration history | Keyed hash through the existing `ConfigurationSnapshotNode.Secret` path |
| Logs | Never, including its length |

## Design decisions

Settled with the user from the mockup:

1. **A third `InitialPasswordSource`**, not a separate switch. The question "where does the password come from" already has two answers on screen; this is the third. Selecting it replaces the generator block rather than sitting beside it, so no stale generator settings remain visible looking as though they applied.
2. **Per Synchronisation Rule**, like every other initial-password setting. A deployment-wide value would be the odd one out, and harder to rotate.
3. **`RequireChangeAtNextSignIn` is the default and is warned about, not forced.** Forcing it would break the testing case this partly exists for, where signing in repeatedly with a known password is the point.

Two decisions taken while planning:

4. **The stored ciphertext is replaced only when a new plaintext is supplied.** Encryption is non-deterministic, so re-encrypting an unchanged password yields different ciphertext; comparing that would make every save look like a change and pointlessly release the accounts parked against the rule. An empty password field therefore means "leave as it is", which is also what makes the field write-only without a special case.
5. **Assessing a supplied password is a new capability, and gets its own result type.** `IPasswordGeneratorService.Assess` answers "what will this generator produce", which says nothing about a value somebody typed. `PasswordGenerationAssessment` cannot be reused as-is either: `EntropyBits` is meaningless for a password an administrator chose and a human being will transcribe, and reporting a figure JIM cannot stand behind is worse than reporting none.

## Phases

### Phase 1: Model and persistence

- `InitialPasswordSource.Static = 2`.
- On `SyncRuleInitialPassword`: `StaticPasswordEncryptedValue` (`string?`) and `StaticPasswordSetAt` (`DateTime?`).
- Extend `SnapshotDeliverySettings()` and `WouldDeliverTheSameAs` for both. `SyncRuleInitialPasswordComparisonCompletenessTests` fails until they are covered; that guard firing is the test-first step, not an obstacle.
- Migration.

### Phase 2: Assessing a supplied password

- `SuppliedPasswordAssessment` (`JIM.Models/Staging`): `Length`, `CharacterClasses`, `Problems`, `IsUsable`. No entropy, deliberately.
- `IPasswordGeneratorService.AssessSupplied(string password, ConnectedSystemPasswordPolicy? targetPolicy)`.
- Where nothing was discovered, it reports no problems and says so; a floor JIM could not read is not a failure to report against.

### Phase 3: Delivery

- `InitialPasswordDeliveryService` takes `ICredentialProtection` and uses the decrypted static value instead of generating, when `Source == Static`.
- The pre-flight check becomes source-aware: `Assess` for a generator, `AssessSupplied` for a static value. A static password that cannot satisfy the target parks the account exactly as an unsatisfiable generator configuration does, and for the same reason.
- A `Static` source with no stored password parks with a configuration fault rather than generating something nobody expects.

### Phase 4: Portal

- Third radio with a *Not recommended* chip; generator block hidden when selected.
- Masked password and confirm fields, blank on load, with the warning alert from the mockup.
- The assessment alert reports the supplied password's length and character categories against the discovered policy.
- A note under **After the password is set** saying why the default matters more here.

### Phase 5: REST and PowerShell parity

- Write-only field on the update DTO; `staticPasswordSet` and `staticPasswordSetAt` on the read DTO.
- `Set-JIMSyncRuleInitialPassword -StaticPassword <securestring>`, `Get-` reporting the two read fields.

### Phase 6: Documentation and changelog

- Amend the security rule in `docs/concepts/passwords.md` (including the "There is no shared initial password" note, which this makes untrue) and in `engineering/DEVELOPER_GUIDE.md` > *3b. Password Channel*.
- `docs/configuration/synchronisation-rules.md`, `docs/powershell/synchronisation-rules.md`.
- Changelog entry leading with the recommendation against it.

## Risks

| Risk | Mitigation |
|---|---|
| The stored password outlives the people who knew it | Documented rotation guidance; `StaticPasswordSetAt` makes staleness visible |
| A save re-encrypts an unchanged password and releases parked work for nothing | Decision 4: ciphertext is replaced only when a new plaintext arrives; covered by a test asserting an unrelated save leaves parked accounts parked |
| The value leaks through a surface added later | Write-only is asserted per surface in tests, not left as a convention |
| The "never stored" rule is weakened silently | The documentation amendment ships in the same change, and is listed as a phase rather than a follow-up |
