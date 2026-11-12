# Changelog

## Unreleased

- Bluesky or LinkedIn answering 2xx without a post id sets the row to "unknown", which Retry refuses, instead of a retryable failure that could post twice.
- The queue list and the due query page in SQL instead of loading every row.
- The pinned connector tries every resolved public address, not only the first.
- README and design notes: LinkedIn tokens expire, outbound calls bypass HTTP proxies, and the link is not cut unless the template alone is over the limit.

## 0.1.0

First public release for Umbraco 17. Post published content to Bluesky, Mastodon and LinkedIn through a retry queue, share links for X and Facebook, a copy-link button, and auto-share on publish for connected networks only.
