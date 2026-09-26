// GiftDeck direct bridge: connects to a TikTok LIVE without TikFinity (no login needed)
// and serves the events on a local WebSocket in the same shape TikFinity uses,
// so GiftDeck can point at ws://localhost:21214/ instead of TikFinity's 21213.
//
//   node bridge.js [username] [port]
const { TikTokLiveConnection, WebcastEvent, ControlEvent } = require('tiktok-live-connector');
const { WebSocketServer } = require('ws');

const username = (process.argv[2] || '').replace(/^@/, '');
if (!username) { console.error('Usage: node bridge.js <tiktok-username> [port]'); process.exit(1); }
const port = Number(process.argv[3] || 21214);

const wss = new WebSocketServer({ port });
const fsLog = require('fs');
const logFile = __dirname + '/bridge.log';
try { fsLog.writeFileSync(logFile, ''); } catch {}
const log = (...a) => {
  const line = [new Date().toLocaleTimeString(), ...a].join(' ');
  console.log(line);
  try { fsLog.appendFileSync(logFile, line + '\n'); } catch {}
};
let live = false;

function send(event, data) {
  const msg = JSON.stringify({ event, data });
  for (const c of wss.clients) if (c.readyState === 1) c.send(msg);
}

wss.on('connection', ws => {
  log('GiftDeck connected to the bridge');
  ws.send(JSON.stringify({ event: 'liveStatusChange', data: { isLive: live, source: 'bridge' } }));
});

const who = d => {
  const u = d.user || {};
  return {
    uniqueId: u.displayId || u.uniqueId || d.uniqueId || '',
    nickname: u.nickname || d.nickname || u.displayId || '',
    profilePictureUrl: (u.avatarThumb && u.avatarThumb.urlList && u.avatarThumb.urlList[0]) || '',
  };
};

// Set GIFTDECK_BRIDGE_DEBUG=1 to keep a few raw TikTok messages in raw.log (to check field names).
// Off by default: they contain viewers' names and chat.
const fs = require('fs');
const debug = process.env.GIFTDECK_BRIDGE_DEBUG === '1';
let rawLeft = 12;
const raw = (type, d) => {
  if (!debug) return;
  if (rawLeft-- <= 0 && type !== 'gift') return;
  try { fs.appendFileSync(__dirname + '/raw.log', type + ' ' + JSON.stringify(d, (k, v) => typeof v === 'bigint' ? v.toString() : (k === 'urlList' || k === 'badgeImageList' || k === 'badges' || k === 'common' || k === 'badgeList' || k === 'payGrade' || k === 'fansClub' || k === 'userAttr' || k === 'followInfo' || k === 'userVipInfo' || k === 'borderList' || k === 'avatarThumb' || k === 'avatarMedium' || k === 'avatarLarge' || k === 'publicAreaCommon' || k === 'publicAreaMessageCommon') ? undefined : v).slice(0, 4000) + '\n'); } catch {}
};

const seenTypes = new Set();

function wire(conn) {
  // Note each kind of TikTok message the first time it shows up, so missing gift types can be spotted.
  conn.on(ControlEvent.DECODED_DATA, (method, decoded) => {
    if (seenTypes.has(method)) return;
    seenTypes.add(method);
    log('message type:', method);
    if (/gift/i.test(method)) raw('type ' + method, decoded && decoded.data ? decoded.data : decoded);
  });
  conn.on(ControlEvent.WEBSOCKET_CONNECTED, ws => {
    ws.on('protoMessageFetchResult', r => {
      for (const m of (r && r.messages) || []) {
        if (m.decodedData || seenTypes.has('undecoded:' + m.method)) continue;
        seenTypes.add('undecoded:' + m.method);
        log('message type (not decoded):', m.method);
      }
    });
  });
  conn.on(WebcastEvent.CHAT, d => {
    if (stale(d)) return;
    raw('chat', d);
    const m = { ...who(d), comment: d.content || d.comment || '' };
    log(`chat  ${m.nickname}: ${m.comment}`);
    send('chat', m);
  });

  conn.on(WebcastEvent.GIFT, d => {
    if (stale(d)) return;
    raw('gift', d);
    const g = d.gift || {};
    const img = (g.image && g.image.urlList) || [];
    const m = {
      ...who(d),
      giftId: Number(d.giftId || g.id || 0),
      giftName: g.name || 'Gift',
      diamondCount: g.diamondCount || 0,
      giftType: g.type || 0,
      repeatCount: d.repeatCount || 1,
      repeatEnd: !!d.repeatEnd,
      giftPictureUrl: img[0] || '',
    };
    log(`gift  ${m.nickname} sent ${m.giftName} x${m.repeatCount} (type ${m.giftType}, end ${m.repeatEnd})`);
    send('gift', m);
  });

  conn.on(WebcastEvent.LIKE, d => send('like', { ...who(d), likeCount: d.likeCount || 1, totalLikeCount: d.totalLikeCount || 0 }));
  conn.on(WebcastEvent.FOLLOW, d => { log('follow', who(d).nickname); send('follow', who(d)); });
  conn.on(WebcastEvent.SHARE, d => send('share', who(d)));
  conn.on(WebcastEvent.SUB_NOTIFY, d => send('subscribe', who(d)));
  conn.on(ControlEvent.ERROR, e => log('TikTok error:', (e && (e.info || e.message)) || e));
  conn.on(WebcastEvent.MEMBER, d => send('member', who(d)));
  conn.on(WebcastEvent.ROOM_USER, d => send('roomUser', { viewerCount: Number(d.total || d.viewerCount || 0), totalViewers: Number(d.totalUser || 0) }));
  conn.on(WebcastEvent.STREAM_END, () => {
    log('LIVE ended');
    live = false;
    send('liveStatusChange', { isLive: false, source: 'bridge' });
    send('streamEnd', {});
  });
}

// Ask TikTok's own web API whether the account is live. This costs nothing on the
// Euler sign server, whose free allowance runs out if we try to connect while offline.
async function liveRoom() {
  try {
    const r = await fetch(`https://www.tiktok.com/api-live/user/room/?aid=1988&uniqueId=${encodeURIComponent(username)}&sourceType=54`, {
      headers: { 'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36' },
    });
    const j = await r.json();
    const d = j && j.data;
    if (d && d.liveRoom && d.liveRoom.status === 2 && d.user && d.user.roomId) return d.user.roomId;
  } catch (e) {
    log('Live check failed:', e.message);
  }
  return null;
}

const sleep = ms => new Promise(r => setTimeout(r, ms));
let connectedAt = 0;

// Messages TikTok replays from before we joined (recent chat) should not fire events again.
const stale = d => {
  const t = Number((d && d.common && d.common.createTime) || 0);
  return t > 0 && t < connectedAt - 3000;
};

// Wait for the LIVE, connect, and reconnect if it drops.
async function run() {
  let wasOffline = false;
  for (;;) {
    const roomId = await liveRoom();
    if (!roomId) {
      if (!wasOffline) log(`@${username} is not live; checking every 30 seconds`);
      wasOffline = true;
      await sleep(30000);
      continue;
    }
    wasOffline = false;

    const conn = new TikTokLiveConnection(username, { enableExtendedGiftInfo: false, fetchRoomInfoOnConnect: false, processInitialData: false });
    wire(conn);
    let wait = 10000;
    try {
      connectedAt = Date.now();
      const state = await conn.connect(roomId);
      live = true;
      log(`Connected to @${username}'s LIVE (room ${state.roomId})`);
      send('liveStatusChange', { isLive: true, source: 'bridge' });
      await new Promise(res => conn.on(ControlEvent.DISCONNECTED, res));
      log('Disconnected from TikTok');
    } catch (e) {
      const msg = String((e && e.message) || e);
      log('Not connected:', msg);
      if (/rate limit/i.test(msg)) wait = 120000; // back off so the free allowance can recover
    }
    live = false;
    send('liveStatusChange', { isLive: false, source: 'bridge' });
    try { await conn.disconnect(); } catch {}
    await sleep(wait);
  }
}

log(`Bridge serving on ws://localhost:${port}/ for @${username}`);
run();
