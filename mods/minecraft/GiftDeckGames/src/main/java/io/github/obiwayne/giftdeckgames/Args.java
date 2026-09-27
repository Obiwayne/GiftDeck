package io.github.obiwayne.giftdeckgames;

import java.util.Arrays;
import java.util.List;
import java.util.Locale;

// The words after "/gdg <game> <action>". GiftDeck sends numbers and choices first and the viewer's name
// last, so the name can have spaces: /gdg bedrockbox tnt 3 Big Bob
final class Args {
    private final String[] words;
    private int next;

    Args(String[] words, int start) {
        this.words = words;
        this.next = start;
    }

    // A whole number, clamped to min..max. Missing or not a number = the default.
    int count(int def, int min, int max) {
        int v = def;
        if (next < words.length) {
            try {
                v = (int) Math.round(Double.parseDouble(words[next]));
                next++;
            } catch (NumberFormatException ignored) {
                // not a number: leave it for the next reader (it may be the viewer's name)
            }
        }
        return Math.max(min, Math.min(max, v));
    }

    // One word from a list (case-insensitive). Missing = the default; a word not in the list is an error.
    String choice(String def, List<String> choices) throws GameException {
        if (next >= words.length) return def;
        String w = words[next].toLowerCase(Locale.ROOT);
        if (w.startsWith("minecraft:")) w = w.substring(10);
        if (!choices.contains(w)) {
            // The last word may be the viewer's name typed without the choice ("/gdg sheepout sheep 3 Bob").
            if (next == words.length - 1) return def;
            throw new GameException("Could not use \"" + words[next] + "\": pick one of " + String.join(", ", choices));
        }
        next++;
        return w;
    }

    // Everything left: the viewer's name, without colour codes. Empty = "Someone".
    String viewer() {
        String name = next < words.length ? String.join(" ", Arrays.copyOfRange(words, next, words.length)) : "";
        next = words.length;
        return cleanName(name);
    }

    static String cleanName(String name) {
        StringBuilder b = new StringBuilder();
        for (int i = 0; i < name.length(); i++) {
            char c = name.charAt(i);
            if (c == '§') { i++; continue; } // colour code and its letter
            if (Character.isISOControl(c)) continue;
            b.append(c);
        }
        String s = b.toString().trim();
        if (s.isEmpty() || s.startsWith("{")) return "Someone"; // "{user}" when a test fires without a viewer
        return s.length() > 24 ? s.substring(0, 24) : s;
    }
}
