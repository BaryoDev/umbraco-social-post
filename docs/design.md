# Design

## Queue and retry

One row per content item and network in `socialPostQueue`, unique on `(ContentKey, Network)`. That unique index is what makes posting idempotent: a second publish finds the row and does nothing. "Force" resets the row to pending and bumps its generation.

Status is `pending`, `sending`, `sent`, `failed` or `cancelled`. Each row holds the attempt count, next attempt time, remote post id and url, and the last error. The row also keeps a snapshot of title, url, summary, image url and tags taken when it was queued, so the worker never touches content. The message text is built from the network template when the row is sent.

A hosted service wakes every `PollSeconds` (or when something is queued) and claims due rows with an atomic `UPDATE ... WHERE Status = 'pending'`. It runs only on the Single or SchedulingPublisher server. On failure the delay is `BaseDelaySeconds * 2^(attempts - 1)`, capped at six hours. A permanent error (bad request, bad token, missing permission) fails the row at once. After `MaxAttempts` the row is `failed`. A row stuck in `sending` for ten minutes (the site died mid-post) is marked `failed` and not retried by itself, because the post may have gone out. A person retries it after checking the network.

`LastError` is cut to 500 characters and every configured credential value is removed from it before it is stored.

A row has a next attempt time. It is set to the time of queuing and moved forward only by the retry backoff.

## Connectors

All implement `ISocialConnector` and use their own typed `HttpClient` from `IHttpClientFactory`. No SDKs. Redirects are not followed, responses are read with a byte cap.

| Network | Endpoint | Auth |
| --- | --- | --- |
| LinkedIn | `POST https://api.linkedin.com/rest/posts`, article content | Access token, `LinkedIn-Version` and `X-Restli-Protocol-Version` headers, author is the member URN (`urn:li:person:`) |
| Bluesky | `com.atproto.server.createSession`, `com.atproto.repo.uploadBlob`, `com.atproto.repo.createRecord` on the PDS | Handle and app password, then a session JWT |
| Mastodon | `POST {instance}/api/v1/media`, then `POST {instance}/api/v1/statuses` | Access token, `Idempotency-Key` header |

Text is trimmed per network: summary first, then tags, then title. The link is never cut. Mastodon counts a link as 23 characters.

## Secrets

Each connector lists its fields and marks which are secret. A value comes from configuration `BaryoDev:SocialPost:Networks:<Name>` first (user secrets or environment variables on the site). If a field is missing there, the value stored from the dashboard is used. Stored secrets are encrypted with an ASP.NET Data Protection protector before they reach the database. Non-secret fields are stored as plain text.

The management API never returns a secret. It returns `isSet` and `source` (`config`, `stored`, `default`, `locked` or `none`). Saving leaves a field alone when the value is blank. A field that comes from configuration cannot be changed from the dashboard. Nothing is logged with a credential in it.

If the Data Protection key ring changes, stored secrets cannot be read and show as not set. Enter them again.

## Share links

`ShareLinks.For(network, text, url)` builds the share page address for `x` (`https://x.com/intent/tweet?text=&url=`), `facebook` (`https://www.facebook.com/sharer/sharer.php?u=`) and `linkedin` (`https://www.linkedin.com/sharing/share-offsite/?url=`). Values are URL-encoded. `GET share/{contentKey}` returns the title, the link and these addresses, after the same Browse permission check as post now. It does not check `DocumentTypes`, because it posts nothing. The dashboard opens them in a new tab with `noopener`.
