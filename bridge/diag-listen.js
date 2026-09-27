// Diagnostic only: joins a LIVE room anonymously for N seconds and counts every message type TikTok sends.
// Doesn't serve anything to GiftDeck.  node diag-listen.js <username> [seconds]
const { TikTokLiveConnection, WebcastEvent, ControlEvent } = require('tiktok-live-connector');
const user = process.argv[2], secs = Number(process.argv[3] || 60);
const counts = {};
const conn = new TikTokLiveConnection(user, { enableExtendedGiftInfo: false, fetchRoomInfoOnConnect: false, processInitialData: true });
conn.on(ControlEvent.DECODED_DATA, (method) => { counts[method] = (counts[method] || 0) + 1; });
conn.on(WebcastEvent.CHAT, d => console.log(new Date().toLocaleTimeString(), 'CHAT from', d.user && d.user.nickname, ':', d.content));
conn.on(WebcastEvent.GIFT, d => console.log(new Date().toLocaleTimeString(), 'GIFT', d.gift && d.gift.name));
conn.on(ControlEvent.ERROR, e => console.log('error:', (e && (e.info || e.message)) || e));
conn.connect().then(s => console.log(new Date().toLocaleTimeString(), 'joined room', s.roomId, '- listening', secs, 's'))
  .catch(e => { console.log('could not join:', e.message); process.exit(1); });
setTimeout(async () => {
  console.log('message counts:', JSON.stringify(counts));
  try { await conn.disconnect(); } catch {}
  process.exit(0);
}, secs * 1000);
