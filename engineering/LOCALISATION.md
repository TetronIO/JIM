# Portal Localisation

> How the portal's text reaches every language, and the rules that keep it there. Issue: [#3030](https://github.com/TetronIO/JIM/issues/3030).

## Contents

- [The rule](#the-rule)
- [How it works](#how-it-works)
- [Writing portal text](#writing-portal-text)
- [Translating](#translating)
- [Guards](#guards)
- [Adding a language](#adding-a-language)
- [Converting existing pages](#converting-existing-pages)
- [Scope](#scope)
- [Decisions](#decisions)

## The rule

**Every string a person reads in the portal comes from a resource file, with a translation in every language, in the same change that adds or edits it.** Translation is part of writing the text, not a follow-up: a new page, a reworded tooltip and a new snackbar message each ship with their German and French (and an en-US entry where the spelling differs) or the build fails. Nobody on the team needs to read German or French for this to work; whoever writes the English writes the translations, following the glossary, and the tests below check what a machine can.

## How it works

| Piece | Where | What it does |
|-------|-------|--------------|
| Resource files | `src/JIM.Web/Resources/<Area>Text.resx` | The English (en-GB) text, one file per area of the portal. These are the *neutral* resources. |
| Translations | `<Area>Text.en-US.resx`, `<Area>Text.de.resx`, `<Area>Text.fr.resx` | The same keys in each language. en-US holds only the strings whose spelling differs. |
| Typed accessors | Generated into `obj/` at build time (`JIM.Web.Resources.<Area>Text`) | `@LayoutText.SignOut` instead of a string key, so a mistyped or deleted key is a compile error. A target in `JIM.Web.csproj` generates them; nothing is checked in and no IDE generator is involved. |
| Languages | `src/JIM.Web/Localisation/UiLanguages.cs` | The languages the portal has, which are released, and the default (en-GB). Only en-GB is released: en-US, German and French wait for the conversion below. Production therefore shows no picker (it hides with one language) and serves en-GB whatever the browser asks for. |
| Request culture | `PortalLocalisation.CreateOptions` | Each portal request runs in the culture cookie's language, else the browser's `Accept-Language`, else the default: British English text in the host's own formats. Those formats come from `LANG`, a documented setting, so a deployment that has chosen no language formats dates and numbers exactly as it did before languages existed. Choosing a language sets both. Applied to the portal only, never to `/api` or `/scim`. |
| Language picker | User menu (`DrawerUserMenu.razor`) | Sets the culture cookie through `/culture/set` and reloads the page; a Blazor Server circuit keeps the culture it started with. |
| MudBlazor's text | `MudBlazorText*.resx`, served by `JimMudLocalizer` | The data grid's filter operators, pagers, close buttons and so on. |
| Plurals | `PluralRules.Choose(count, one, other)` | Picks the form by the language's rule (French counts 0 as singular). |
| Glossary | `engineering/localisation/glossary.json` | The word for each JIM term in each language, enforced by a test. |

Resource lookup falls back through parent cultures: de-AT is served `de`, en-AU is served the neutral en-GB. A string missing from a translation falls back to English, never to a blank or a key, which is why an unreleased language can be partly translated without breaking anything.

## Writing portal text

- **Markup:** `<MudText>@HomeText.GettingStarted</MudText>`, `Title="@CommonText.ColumnName"`. `JIM.Web.Localisation` and `JIM.Web.Resources` are in `_Imports.razor`.
- **Values in a sentence:** a placeholder, never concatenation. Word order differs between languages: `string.Format(CultureInfo.CurrentCulture, HomeText.NoSchedulesMatch, search)` with `No Schedules match "{0}"`. Concatenating fragments ("Deleted " + n + " objects") cannot be translated.
- **Counts:** two keys, `...One` and `...Other`, chosen with `PluralRules.Choose`. Never append "s".
- **One key per meaning, not per word.** "Activity" as a menu heading and "Activity" as a menu item are two keys; German or French may need different words for them, and a shared key forces one.
- **Reuse `CommonText`** for words that genuinely mean the same everywhere: Cancel, Never, Enabled, a Name column.
- **Name the key for what it is** (`SignOutDialogTitle`, `NoSchedulesYetHint`), and give it a comment in the neutral file wherever a translator could misread it: where it appears, what `{0}` holds, a grammatical constraint ("German: dative case").
- **New file per area**, named `<Area>Text.resx`, when an area has more than a handful of strings of its own. Keeping areas apart keeps parallel branches out of each other's files.
- **Dates and numbers** format in `CultureInfo.CurrentCulture`, which request localisation sets. Keep `ToFriendlyDate()` and friends; their month names follow the language.
- **Layout:** leave room for German, which runs about 30% longer than English, and write new CSS with logical properties (`margin-inline-start`, `padding-inline-end`, `inset-inline-start`) rather than left and right, so a right-to-left language does not need a stylesheet rewrite.
- **Not text:** CSS classes, routes, query keys, log messages, exception messages that only reach logs, and identifiers stay in code. A product name, a protocol or a font name that the scanner misreads takes an `i18n: exempt - <why>` comment on its line or one of the two above.

## Translating

Write German and French as a fluent technical writer would for an administrator: formal address in German (*Sie*), standard French typography (a space before `:`, `;`, `?` and `!`, *guillemets* « » around quoted text, the typographic apostrophe ’). German quotes use „…“. Keep every placeholder exactly as the English has it.

**Use the glossary's word for every JIM term.** The proper-noun vocabulary (Connected System, Synchronisation Rule, Metaverse Object and the rest) is a product contract in English, and each language needs one fixed word for each term or the portal reads as several products. `glossary.json` holds a pattern per language, and `EveryLanguage_UsesTheGlossaryTermForEachJimTerm` fails a translation that uses anything else. Adding a JIM term to the portal means adding it to the glossary in the same change. German and French write these terms as ordinary nouns, lower case mid-sentence in French, rather than in English Title Case.

**American English translates the vocabulary** ("Synchronization Rule", "Organization"); the decision and its reasons are recorded on the issue. The machine surfaces (REST field names, PowerShell cmdlet and parameter names, C# identifiers) and the documentation site stay British.

An in-country review of a language before a customer deployment is welcome and is the place to raise a better word; change the glossary and every file together, since the test checks them against each other.

## Guards

All of these run in `dotnet test test/JIM.Web.Tests/`, and so in `build-and-test` on every pull request.

| Test | Fails when |
|------|------------|
| `HardCodedTextConventionTests` | A portal file has more hard-coded user-visible text than its count in `hard-coded-text-baseline.json`, or a new file has any. Also fails when a count goes *down* without the baseline being lowered, so progress is locked in. |
| `LocalisationResourceTests.EveryTranslation_KeepsTheEnglishStringsPlaceholders` | A translation drops, adds or renumbers a `{n}` (which would throw at run time). |
| `...EveryTranslation_AnswersToAnEnglishString` | A translation's key no longer exists in English (renamed or removed). |
| `...ReleasedLanguages_TranslateEveryString` | A released language is missing any key. |
| `...AmericanEnglish_OverridesEveryBritishSpelling` | An English string with a British spelling has no en-US entry, or the en-US entry still has it. |
| `...EveryLanguage_UsesTheGlossaryTermForEachJimTerm` | A translation of a string containing a JIM term does not use the glossary's word for it. |
| `...MudBlazorText_CarriesEveryKeyMudBlazorAsksFor` | A MudBlazor upgrade added component text JIM does not translate. |
| `...EveryCultureFile_HasANeutralFile_AndAnOfferedLanguage` | A translation file has no English file, or is for a language not in `UiLanguages`. |
| `LocalisedRelativeTimeTests`, `PluralRulesTests`, `UiLanguagesTests` | The shared machinery misbehaves. |

The JIM.Web test assembly runs in en-GB (`AssemblyDiagnostics.cs`), so existing tests asserting on English text pass on any machine; a test about another language sets `[SetUICulture]` itself.

**The scanner is a heuristic.** It reads markup text, text attributes (`Title`, `Label`, `Placeholder`, `aria-label`, anything ending in `Text`, and so on), and prose string literals in C# (a phrase, or a single capitalised word handed to a snackbar, a message box or a text property). It skips log and exception messages. A false positive costs an exemption comment; tune `HardCodedTextScanner` rather than exempting the same shape repeatedly.

To list every remaining hard-coded string, file by file, as a work list:

```bash
JIM_LOCALISATION_REPORT=/tmp/i18n-report.txt dotnet test test/JIM.Web.Tests --filter Name=WriteHardCodedTextReport
```

After converting text, lower the baseline (never raise it to make room):

```bash
JIM_UPDATE_LOCALISATION_BASELINE=1 dotnet test test/JIM.Web.Tests --filter HardCodedTextConventionTests
```

## Adding a language

1. Add it to `UiLanguages.All` with `IsReleased: false`. Use the neutral culture (`it`, `es`) unless spelling differs by region, as English does.
2. Add `<Area>Text.<culture>.resx` for every neutral file, starting with `MudBlazorText`, `LayoutText` and `CommonText`.
3. Add the language's word for every term to `glossary.json`.
4. If the language has more than two plural forms (Polish, Russian, Arabic), extend `PluralRules` and add the extra keys before translating any counted string.
5. Development builds offer it in the picker marked "(preview)"; check pages in it as it fills in.
6. When `ReleasedLanguages_TranslateEveryString` would pass for it, set `IsReleased: true`, add it to the Languages table in `docs/administration/portal-language.md`, and add a changelog entry. The first language released publishes that page from its draft, `engineering/localisation/portal-language-page-draft.md`.

en-US is released the same way, once the baseline reaches zero: before then an American user would read "Synchronization Rules" in the menu and "Synchronisation" in every page body, which looks broken.

A right-to-left language also needs `dir="rtl"` on the page (from the culture's `TextInfo.IsRightToLeft`), MudBlazor's RTL mode, and a sweep of `site.css` for physical left/right properties.

## Converting existing pages

The baseline started at 7,635 hard-coded strings in 333 files. No language beyond en-GB can be released until it reaches zero, so converting areas ([#3040](https://github.com/TetronIO/JIM/issues/3040)) is the release path for all three. Work an area at a time (one resource file, one pull request): take the area's lines from the report, move them into its resource file with German, French and en-US entries, run the tests, lower the baseline. The largest files are the Activity pages, the Connected System tabs, the Synchronisation Rule Attribute Flow tab and the causality display maps (`OutcomeDisplayMap.cs`, `CausalitySummaryBuilder.cs`); shared components (`VirtualisedDataGrid`, `TableObjectCount`, `TableEmptyState`, `SearchField`) reach every page and pay off first.

## Scope

| Surface | Localised | Why |
|---------|-----------|-----|
| Portal pages, components, dialogs, snackbars | Yes | The product's user interface. |
| MudBlazor's own component text | Yes | Part of every page. |
| Error and sign-in problem pages | Yes (being converted) | Shown to users. |
| Activity and Run Profile Execution Item messages | Not yet | Stored in the database as rendered English. They are to be stored as message keys plus arguments so history renders in each viewer's language; that changes the synchronisation write path and the API response shape, so it is its own issue ([#3039](https://github.com/TetronIO/JIM/issues/3039)). |
| REST API and SCIM | No | A machine-readable contract under semantic versioning. Request localisation is not applied to `/api` or `/scim`, so a client's `Accept-Language` cannot change a response. |
| PowerShell module | No | Cmdlet names, parameters and output are a scripting contract. Help text may follow later. |
| Logs | No | Read by support and engineering. |
| Documentation site | No | British English. |

## Decisions

- **Typed accessors over `IStringLocalizer`.** `IStringLocalizer<T>["Key"]` is a string lookup: a typo or a removed key renders the key name on screen and nothing fails. Generated accessors make both compile errors, need no injection in 300 components or their tests, and read the same culture. MudBlazor's localiser, which must be an `IStringLocalizer`-shaped class, reads the same files.
- **A culture cookie, not a stored per-user preference.** It is the framework's own mechanism, needs no migration and no REST or PowerShell surface, and matches how the portal's other display preferences are kept (per browser). A per-user setting can replace it later without changing how text is written.
- **Released only when complete.** A half-translated page is worse than an English one, so a language is offered in production only when every string has a translation, and the test enforces it.
- **Neutral German and French.** `de` and `fr` rather than `de-DE` and `fr-FR`, so Austria, Switzerland, Belgium and Canada are served the same translation through parent-culture fallback.
