---
title: Portal Language
description: "Which languages the JIM portal is shown in, how it chooses one for each person, and how to change yours."
type: concept
audience: administrator
---

# Portal Language

The JIM portal can be shown in more than one language, and each person sees it in their own. JIM picks a language from the browser's preferred languages the first time someone signs in, and anyone can choose another from the user menu; the choice is remembered in that browser. Only the portal's own wording changes: the data JIM synchronises, the names you give to Connected Systems, Synchronisation Rules and Schedules, and the REST API and PowerShell module are the same whichever language a person reads the portal in.

```jim-diagram
archetype: decision
title: How the portal chooses a language
desc: "When the portal loads, JIM first checks whether this browser has a language chosen from the user menu; if it has, the portal uses it. If not, JIM checks whether one of the browser's preferred languages is one the portal offers, matching a regional variant such as German (Austria) to German; if one is, the portal uses it. Otherwise the portal is shown in English (United Kingdom)."
start: { label: The portal loads }
questions:
  - { id: chosen, label: "Language chosen in this browser?", sub: from the user menu }
  - { id: browser, label: "A browser language the portal offers?", sub: regional variants match their language }
outcomes:
  - { id: usechosen, label: That language, sub: remembered for a year, good: true }
  - { id: usebrowser, label: The browser's language, good: true }
  - { id: fallback, label: English (United Kingdom), sub: the default }
answers:
  - { from: chosen, to: usechosen, label: "yes" }
  - { from: chosen, to: browser, label: "no" }
  - { from: browser, to: usebrowser, label: "yes" }
  - { from: browser, to: fallback, label: "no" }
```

## :material-translate: Languages

| Language | Shown in the picker as | Notes |
|----------|------------------------|-------|
| English (United Kingdom) | English (UK) | The default, and what any other English-speaking region sees (Australia, Ireland, India and so on). |
| English (United States) | English (US) | American spelling: "Synchronization Rule", "authorization", "color" <!-- conformance: allow LANG-2 "examples of the American spelling this language uses" -->. |
| French | Français | One French for every French-speaking region (France, Belgium, Canada, Switzerland and so on). |

JIM offers a language only once every part of the portal has been translated into it, so nobody is shown a page that is partly in one language and partly in another.

## :material-account-cog-outline: Change your language

Select your name at the bottom of the navigation menu, then **Language**, and the language you want. The page reloads in the language you chose. The choice is stored in a cookie in that browser, so it applies to every page and lasts a year; another browser or computer starts from its own preferred languages again.

## :material-lock-outline: What stays in English

Some text is not part of the portal's wording, and reads the same in every language:

- **Your own data and configuration.** Object names and attribute values, and the names and descriptions you give to Connected Systems, Synchronisation Rules, Schedules and everything else you create.
- **Activity messages recorded by JIM 1.0.** JIM records the messages and names it writes to an Activity so they can be shown in each reader's language. Activities recorded by JIM 1.0 releases were stored as English and stay that way.
- **Other systems' own words.** An error a directory, a database or an identity provider returns is shown as that system worded it.
- **The REST API and the PowerShell module.** Their field names, values and messages are a fixed contract that scripts and integrations depend on, so they do not change with anyone's language. A client's `Accept-Language` header has no effect on them.
- **This documentation site,** which is written in British English.

## :material-calendar-clock: Dates and numbers

Dates and numbers are written the way the chosen language writes them, with month names in that language. Times are shown in each person's local time, whatever their language.

## :material-link-variant: Related

- [Working with Lists](portal-lists.md): how the portal's lists behave, in every language.
