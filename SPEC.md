# Clergy roster maintenance contracts

## §G

Apply Discord clergy commands to configured GuildTag roster; preserve unrelated roster data and verify persistence before success feedback.

## §C

- C1: .NET 10, Discord.Net, Playwright Chromium, HtmlAgilityPack.
- C2: Credentials stay in deployment environment. Browser sessions stay ephemeral.
- C3: Startup retains existing reaction-boundary replay behavior. Historical reconciliation requires current roster review.

## §I

- I1: Flat environment variables in `.env.example`; caller loads dotenv files.
- I2: Native GuildTag account login; same-origin `/api/forum-thread/{id}/1/` reads and `/api/forum-post/` edits.
- I3: First post (`postNumber = 1`) owns active roster; later posts remain untouched.

## §V

- V1: Roster reads require matching thread identity, view permission, one first post, edit permission, and nonempty source. Missing error banner never proves access. Expired sessions reauthenticate.
- V2: Before edit, reread source and reject intervening changes; require local backup. After one POST, verify exact source and post identity through fresh reads before success. Failed or uncertain saves never count as success without read-back.
- V3: Named, dated Curate quest-start announcements are ignored; actual rank assignments remain actionable.
- V4: GuildTag navigation and API calls are paced; reads honor bounded Retry-After retries; uncertain writes are never blindly repeated.
- V5: Roster mutations preserve unrelated members, LOTH markers, divine headings, images, surrounding content, and later posts.

## §T

id|status|task|cites
---|---|---|---
T1|done|Replace banner/editor assumptions with authenticated source API and verified edits|I2,I3,V1,V2,V4
T2|done|Recognize named and dated quest-start announcements|V3
T3|done|Add offline Chromium and roster regression checks|V1,V2,V3,V4,V5

## §B

id|date|cause|fix
---|---|---|---
B1|2026-09-08|Site migration left old host configured; absent permission banner falsely implied authenticated editable thread|V1; update deployment URL
B2|2026-09-08|SPA URL predicate could report save success before persistence; timeout also assumed success|V2
B3|2026-09-08|Quest-start regex rejected character prefixes, Started, and dates|V3
B4|2026-09-08|Unpaced navigation/API reads encountered GuildTag HTTP 429 during diagnosis|V4
B5|2026-09-08|Regeneration replaced untouched blank priest slot with Vacant during recovery preview|V5; preserve unchanged rank DOM
