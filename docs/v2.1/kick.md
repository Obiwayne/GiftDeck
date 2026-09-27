# Kick support (v2.1)

GiftDeck can read a Kick channel next to TikTok: Kick chat, subs, gifted subs, follows and Kicks gifts
run the same events (rules) as TikTok's, also while the streamer is live on both at once.

Research and tests done on 2026-09-27 from this PC (Windows 11, .NET 8).

## The two ways to get Kick events

### 1. Kick's official public API (dev portal)

- Apps are registered at kick.com, Settings, Developer (docs: docs.kick.com). Auth is OAuth 2.1
  with PKCE (`id.kick.com`); a User Access Token needs scopes such as `events:subscribe`, and an
  App Access Token (client credentials) can subscribe to events of any channel by its user id.
- Events: `chat.message.sent`, `channel.followed`, `channel.subscription.new`, `.renewal`, `.gifts`,
  `kicks.gifted`, `livestream.status.updated`, `livestream.metadata.updated`, `moderation.banned`,
  `channel.reward.redemption.updated`.
- Delivery is webhook only: `POST /public/v1/events/subscriptions` takes `"method": "webhook"`
  (the only value), and the webhook URL is set on the app in the dev portal. Kick POSTs every
  event to that public HTTPS URL. There's no WebSocket or polling alternative for events.
- For a desktop app that means: every user registers their own developer app (client id and secret),
  and GiftDeck needs a public URL that reaches their PC (a tunnel such as ngrok or Cloudflare Tunnel,
  or a relay server run by us that every user's events pass through). It also gives follows with
  names and a documented, supported contract.

### 2. Kick's public Pusher feed (read-only, no login)

What kick.com's own chat uses for signed-out viewers. This is what StreamToEarn-style tools and most
community libraries use.

- Channel name to ids: `GET https://kick.com/api/v2/channels/<name>` returns `id` (channel id),
  `chatroom.id`, `slug`, `user.username` and `livestream` (null when offline).
- Which Pusher app: `POST https://web.kick.com/api/v1/realtime/channels/<channel id>/chat/connection`
  with `{"client":{"id":"…","type":"web"},"capabilities":{"accepted_providers":[{"provider":"pusher"}]}}`
  answers `{"provider":"pusher","credentials":{"app_key":"32cbd69e4b950bf97679","cluster":"us2"}}`.
  (Kick's own web player now asks for `centrifugo` too and is given
  `wss://realtime.us-east-1.platform.kick.com/connection/websocket`; Pusher is still offered and works.)
  GiftDeck asks each time it connects and falls back to that key and cluster.
- Socket: `wss://ws-us2.pusher.com/app/32cbd69e4b950bf97679?protocol=7&client=js&version=8.4.0&flash=false`,
  then `{"event":"pusher:subscribe","data":{"auth":"","channel":"…"}}` per channel. Public channels need
  no auth. Pusher sends `pusher:ping` (answer `pusher:pong`); `activity_timeout` is 120 s.
- Channels and events (seen in kick.com's current JS bundle and in live captures on 2026-09-27: 12 busy
  channels for 25 minutes plus 16 more for 20 minutes; 632 Kicks gifts, 5 subs, 1 gifted sub, 0 FollowersUpdated):

| Pusher channel | Event | GiftDeck event |
|---|---|---|
| `chatrooms.<chatroom id>.v2` | `App\Events\ChatMessageEvent` (`type` `message` or `reply`; `content`, `sender.username`, `created_at`) | chat |
| `chatrooms.<chatroom id>.v2` | `App\Events\SubscriptionEvent` (`username`, `months`) | subscribe |
| `chatroom_<chatroom id>` | `GiftedSubscriptionsEvent` (`gifter_username`, `gifted_usernames`, `gifted_total`, `chunk_details` null or split for big gifts); older name `App\Events\GiftedSubscriptionsEvent` | subscribe with count |
| `channel_<channel id>` | `KicksGifted` (`sender.username`, `gift.gift_id`, `gift.name`, `gift.amount`, `gift.tier`, `message`) | gift |
| `channel_<channel id>` | `GoalProgressUpdateEvent` with `type: "followers"` (`current_value`) | follow (unnamed) |
| `channel.<channel id>` | `App\Events\FollowersUpdated` (older, named) | follow |
| `channel.<channel id>` | `App\Events\StreamerIsLive`, `App\Events\StopStreamBroadcast` | live status only |

  Seen but ignored (so nothing counts twice): `ChannelSubscriptionEvent`, `NewSubscriberUpdatedEvent`,
  `NewActivityFeedEvent`, `ChatMessageSentEvent` (all sent next to `SubscriptionEvent`),
  `KicksLeaderboardUpdated` (next to each `KicksGifted`), `GiftsLeaderboardUpdated`, `PollUpdateEvent`, `RewardRedeemedEvent`, `MessageDeletedEvent`,
  `UserBannedEvent`, pinned message and chat settings events.

- Cloudflare: kick.com is behind Cloudflare. In the MyInstants work .NET's `HttpClient` got 403 where
  curl.exe worked. Today, from this PC, both a plain `HttpClient` (no browser headers) and curl.exe got
  HTTP 200 for `/api/v2/channels/<name>` and for the web.kick.com call. Because that can change,
  `KickApi` sends browser-like headers and, if the answer isn't JSON (403 or a challenge page), repeats
  the call with Windows' own `C:\Windows\System32\curl.exe`. The WebSocket (pusher.com) isn't behind
  Kick's Cloudflare.

### Choice: the Pusher feed

A free desktop app can't ask every streamer to register a Kick developer app and expose a public
webhook URL, and read-only is all GiftDeck needs. The Pusher feed needs only the channel name, works
from a PC with no open ports, gives chat, subs, gifted subs and Kicks gifts with names, and is what
Kick's own site uses.

Trade-offs:
- Unofficial: Kick can change event names, the app key or move signed-out viewers to Centrifugo.
  The app key is asked for on each connect, and the parser ignores anything it doesn't know.
  If Pusher is dropped one day, Centrifugo (same channel names, a guest token from
  `POST web.kick.com/api/v1/realtime/auth/connection`) or the official API are the ways forward.
- Follows: Kick's public feed no longer names followers (`FollowersUpdated` didn't show up once in
  the captures above, while those channels' follower counts kept rising). GiftDeck counts follows from the
  follower goal: a channel with a follower goal on Kick gets its count every few seconds, and each rise
  fires one "New follower" event per new follower (at most 5 per update, never again for the same count after
  an unfollow). The name is "Someone". Without a follower goal on the Kick channel, Kick follows don't
  fire. Named follows need the official API (`channel.followed` webhook).
- Read-only: GiftDeck never sends anything to Kick chat (it has no login to do so).

## How Kicks map to coins

Kicks are Kick's own currency. Viewers pay roughly $1 for 100 Kicks; TikTok viewers pay roughly $1 to $1.40
for 100 coins (depending on the pack). Both are about a cent each, and Kick's gift prices (1, 10, 50, 100,
500, 1000 Kicks, seen in the capture) sit in the same range as TikTok's (1 to 1000+ coins), so GiftDeck
counts 1 Kick = 1 coin (not re-checked against today's prices in each country): a Kicks gift is a `gift` event with `GiftName` = the Kick gift's name
("Rage Quit"), `Diamonds` = its Kicks amount (500), `RepeatCount` = 1. So "Any gift worth 100+ coins"
works the same for both, and `{coins}` in a TTS line or alert says 500. The live panel shows the amount
in Kicks ("500 Kicks"). Kick gifts are never added to GiftDeck's TikTok gift list; a rule for one named
TikTok gift won't fire for a Kick gift (use "Any gift" with a coin range, which also can be limited to Kick).

Gifted subs: one `subscribe` event with `RepeatCount` = number of subs ({count}), described as
"bob gifted 5 subs". A normal sub is one `subscribe` event.

## In GiftDeck

- `LiveEvent.Platform`: `"tiktok"` (default) or `"kick"`.
- `RuleTrigger.Platform`: `""` any (default, so existing rules fire for both), `"tiktok"` or `"kick"`.
  Rule editor: "Coming from: TikTok or Kick / TikTok only / Kick only", shown once Kick is on.
  The summary says "(Kick only)" / "(TikTok only)".
- Settings: `KickEnabled`, `KickChannel` in settings.json.
- `Services/Kick/KickApi.cs` (lookups with the curl.exe fallback), `KickParser.cs` (payload to LiveEvent,
  de-duplication by message id, gifted-sub chunks, follower goal), `KickService.cs` (connect, subscribe,
  ping, reconnect with back-off 2, 5, 10, 30 s, restart when the channel name changes).
- Hub: `Hub.Kick`; its events go to `Rules.Handle` like TikTok's (separate source, nothing doubled).
- Stream Setup: Kick card (switch, channel name, "Connected to kick.com/<name> ✓", last error).
- Status strip and setup wizard's ready check: a Kick line, only when Kick is switched on. The wizard
  asks for the channel name only if Kick is on and the name is empty.
- Live panel: Kick rows carry a green KICK mark; TikTok rows get a TikTok mark while Kick is on.

## Tests

`tests/Kick` (KickHarness):
- `KickHarness.exe unit` parses `tests/Kick/samples.jsonl` (real frames captured from live Kick channels
  on 2026-09-27) and checks chat, Kicks gifts, a real sub, follower-goal follows, the ignored duplicates,
  a real gifted sub and stream-end, gifted subs under the older name and in chunks (shapes from
  kick.com's JS; not seen live), named follows (older shape; not seen live), rule
  matching with the platform filter, templates.
- `KickHarness.exe live <channel> [seconds]` connects to a live channel read-only and prints each parsed
  event and which rules would fire (rules with no actions).
