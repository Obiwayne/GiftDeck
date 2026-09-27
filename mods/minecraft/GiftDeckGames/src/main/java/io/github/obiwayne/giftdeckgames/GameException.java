package io.github.obiwayne.giftdeckgames;

// A command that couldn't do anything. The message goes back to the sender (GiftDeck, over RCON) as-is and
// always starts with "Could not", which GiftDeck recognises as a failed command.
final class GameException extends Exception {
    GameException(String message) {
        super(message);
    }
}
