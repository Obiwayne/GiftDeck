# Records raw Kick Pusher frames (read-only, no login) from live channels, for samples.jsonl.
# PYTHONIOENCODING=utf-8 python capture.py <seconds> <out.jsonl> <channel> [channel ...]   then curate.py
import asyncio, websockets, json, sys, time, subprocess
secs = int(sys.argv[1]); out = sys.argv[2]; slugs = sys.argv[3:]
chans = []
for s in slugs:
    d = json.loads(subprocess.run(["curl.exe", "-s", f"https://kick.com/api/v2/channels/{s}"], capture_output=True).stdout)
    cid, rid = d["id"], d["chatroom"]["id"]
    chans += [f"chatrooms.{rid}.v2", f"chatrooms.{rid}", f"chatroom_{rid}", f"channel.{cid}", f"channel_{cid}"]
    print(s, cid, rid, flush=True)
counts = {}
async def main():
    url = "wss://ws-us2.pusher.com/app/32cbd69e4b950bf97679?protocol=7&client=js&version=8.4.0&flash=false"
    f = open(out, "a", encoding="utf8", buffering=1)
    async with websockets.connect(url) as ws:
        await ws.recv()
        for c in chans:
            await ws.send(json.dumps({"event": "pusher:subscribe", "data": {"auth": "", "channel": c}}))
        end = time.time() + secs
        chats = 0
        while time.time() < end:
            try:
                m = await asyncio.wait_for(ws.recv(), 5)
            except asyncio.TimeoutError:
                continue
            j = json.loads(m)
            ev = j.get("event", "")
            key = j.get("channel", "") .split(".")[0].split("_")[0] + ":" + ev
            counts[key] = counts.get(key, 0) + 1
            if ev == "App\\Events\\ChatMessageEvent":
                chats += 1
                if chats <= 5 or '"type":"message"' not in j.get("data", ""):
                    f.write(m + "\n")
                    if '"type":"message"' not in j.get("data", ""): print("NONMSG CHAT", m[:300], flush=True)
                continue
            if ev.startswith("pusher"):
                continue
            f.write(m + "\n")
            print(ev, j.get("channel"), j.get("data", "")[:300], flush=True)
        print(json.dumps(counts, indent=1), flush=True)
asyncio.run(main())
