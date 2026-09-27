// Shared by the overlay pages. GD.listen(fn) calls fn with every message from GiftDeck.
// On its own a page reads GiftDeck's live stream (/events). Inside the all-in-one page (?embed=1) the
// all-in-one page passes its messages down instead, so one link keeps a single connection open
// however many parts it shows (browsers only allow a few open connections to one server).
window.GD = (function () {
  const q = new URLSearchParams(location.search);
  const embedded = q.has('embed') && window.parent !== window;
  return {
    embedded,
    param: name => q.get(name),
    listen(fn) {
      if (embedded) {
        addEventListener('message', e => {
          if (e.origin !== location.origin || typeof e.data !== 'string') return;
          try { fn(JSON.parse(e.data)); } catch (err) { console.error(err); }
        });
        // Tell the all-in-one page we're ready for the current state.
        parent.postMessage('gd-ready', location.origin);
        return;
      }
      const es = new EventSource('/events');
      es.onmessage = e => fn(JSON.parse(e.data));
    },
  };
})();
