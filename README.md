# BaryoDev.Umbraco.SocialPost

Post Umbraco 17 content to LinkedIn (personal profile), Bluesky and Mastodon. Free, MIT licensed. Targets Umbraco 17 and .NET 10.

When content of a configured document type is published for the first time, its title, link, summary and image go into a queue. A background service posts it to each enabled network, retries with backoff, and gives up after a set number of attempts. You can also post any published item from the backoffice.

Status: early. The connectors have not been run against the live networks yet. Check each one with a test account before you rely on it.

## Install

```
dotnet add package BaryoDev.Umbraco.SocialPost
```

The package creates its two tables on first start through an Umbraco package migration.

## Configure

In `appsettings.json` (placeholders only, keep real values out of the repo):

```json
{
  "BaryoDev": {
    "SocialPost": {
      "Enabled": true,
      "DocumentTypes": [ "blogPost" ],
      "TitleAlias": "",
      "SummaryAlias": "summary",
      "ImageAlias": "heroImage",
      "TagsAlias": "tags",
      "PostOnFirstPublish": true,
      "SiteBaseUrl": "https://www.example.com",
      "ImageHosts": [],
      "MaxAttempts": 5,
      "BaseDelaySeconds": 60,
      "PollSeconds": 30,
      "Networks": {
        "LinkedIn": { "Enabled": true, "AuthorUrn": "urn:li:person:<id>" },
        "Bluesky": { "Enabled": true, "Handle": "<name>.bsky.social", "ServiceUrl": "https://bsky.social" },
        "Mastodon": { "Enabled": true, "InstanceUrl": "https://mastodon.example" }
      }
    }
  }
}
```

Secrets go in user secrets or environment variables, never in the file:

```
BaryoDev__SocialPost__Networks__LinkedIn__AccessToken
BaryoDev__SocialPost__Networks__Bluesky__AppPassword
BaryoDev__SocialPost__Networks__Mastodon__AccessToken
```

Or enter them in the dashboard (Settings section, "Social post"). Those are stored encrypted with ASP.NET Data Protection. The dashboard never shows a stored secret again, only "set" or "not set". A value in configuration wins over a stored one. If the Data Protection key ring is lost, stored secrets cannot be read and have to be entered again.

Every network also takes a `Template` with `{title}`, `{url}`, `{summary}` and `{tags}`. The text is trimmed to the network limit by cutting the summary first, then the tags, then the title. The link is not cut unless the template alone is over the network limit.

## How posting works

- One queue row per content item and network. A second publish, or a second click, does not post again. Tick "Post again" to force it.
- A temporary error (timeout, 429, 5xx) is retried after `BaseDelaySeconds * 2^(attempts - 1)`, up to six hours. A permanent error (bad token, bad request) fails the row at once.
- If the final call that creates a post times out or answers 5xx, the row is marked failed with "Unknown outcome" and is not retried, because the post may exist (Mastodon is the exception, it sends an idempotency key). Connection failures before the request is sent, and 429, are retried.
- If Bluesky or LinkedIn answers 2xx to the create call but no post id can be read from the answer, the row gets the status "unknown" with the same "Unknown outcome" text. Retry refuses it. Check the network, then use "Post again" if the post is not there.
- Before a delayed or retried row is sent, the content is checked. If it is no longer published, the row is cancelled.
- Post now needs the Browse permission on the node, and the document type must be in `DocumentTypes`.
- A row stuck in "sending" after a crash is marked failed, not retried by itself, because the post may have gone out.
- The last error is cut to 500 characters and has credential values removed.
- If a network's secret comes from configuration, its address (`ServiceUrl`, `InstanceUrl`) can only come from configuration or the built-in default. The dashboard refuses to change it, so a configured secret is never sent to an address typed into the backoffice.
- Changing a network's address (Bluesky `ServiceUrl`, Mastodon `InstanceUrl`) from the dashboard clears that network's stored secrets, and a stored secret is only used for the host it was saved for.
- Images are fetched only from the content's own host, the host of `SiteBaseUrl`, or hosts in `ImageHosts`, and only from public addresses. A site on localhost posts without images.
- Outbound calls to Bluesky and Mastodon and the image fetch ignore HTTP proxy settings. The connection is pinned to an address that passed the public-address check, and a proxy would resolve the name itself. A server that can only reach the internet through a proxy cannot use these. LinkedIn calls do use the proxy.
- In a load-balanced setup only the scheduling publisher drains the queue.

## Networks

| Network | Fields | Notes |
| --- | --- | --- |
| LinkedIn | `AuthorUrn`, `AccessToken`, `ApiVersion` | Posts as a member, so `AuthorUrn` must start with `urn:li:person:`. Link is attached as an article. LinkedIn access tokens expire. There is no refresh, so enter a new token before the old one runs out. |
| Bluesky | `Handle`, `AppPassword`, `ServiceUrl` | Link card with the image as thumbnail when it is under 950 KB. |
| Mastodon | `InstanceUrl`, `AccessToken`, `Visibility` | Image is uploaded and attached. |

## Share links

The "Post now" box has a "Share links" button. For the picked content it shows buttons that open the share page of X (text and link), Facebook (link only) and LinkedIn (link only), and a "Copy link" button that copies the title and the link. These open the network's own page and need no credentials. Copy link appears only when the browser allows clipboard access.

## Other networks

`ISocialConnector` is the extension point. A package can add a network by registering a typed `HttpClient` for its connector and the connector itself as an `ISocialConnector`. The dashboard lists the connectors that are registered. `QueueService.Enqueue` is public, so a package can also queue a post from its own trigger.

## License

MIT.
