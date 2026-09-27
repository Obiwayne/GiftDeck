# Picks a small, varied set of real frames from capture.py output for samples.jsonl (the unit test's input).
# python curate.py out.jsonl capture1.jsonl [capture2.jsonl ...]
import json, sys
out, ins = sys.argv[1], sys.argv[2:]
lines = [l.strip() for f in ins for l in open(f, encoding="utf8") if l.strip()]
limits = {"App\\Events\\ChatMessageEvent": 40, "KicksGifted": 20, "KicksLeaderboardUpdated": 2,
          "App\\Events\\MessageDeletedEvent": 2, "App\\Events\\UserBannedEvent": 2, "RewardRedeemedEvent": 2}
seen, keep, goal_channel = {}, [], None
for l in lines:
    j = json.loads(l)
    ev, ch = j.get("event", ""), j.get("channel", "")
    if ev == "GoalProgressUpdateEvent":
        if '\\"type\\":\\"followers\\"' not in l: continue
        goal_channel = goal_channel or ch
        if ch != goal_channel or seen.get(ev, 0) >= 30: continue
    elif ev in limits and seen.get(ev, 0) >= limits[ev]:
        continue
    elif ev not in limits and seen.get(ev, 0) >= 10:
        continue
    seen[ev] = seen.get(ev, 0) + 1
    keep.append(l)
open(out, "w", encoding="utf8", newline="\n").write("\n".join(keep) + "\n")
print(len(keep), "frames:", seen)
