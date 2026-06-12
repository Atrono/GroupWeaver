// bridge.js - message bridge facade between the page and the host.
//
// Under the Avalonia NativeWebView (WebView2 on Windows) the host injects a
// global invokeCSharpAction(string) which raises WebMessageReceived in .NET.
// The host calls into the page via InvokeScript("window.bridge.dispatch({...})").
//
// Seam for a future Playwright harness: define window.__bridgeSendShim(text)
// before this script runs and the bridge will use it instead - no other code
// in the bundle talks to the host directly.
(function () {
  'use strict';

  var queue = [];
  var flushTimer = null;

  function rawSend(text) {
    if (typeof window.invokeCSharpAction === 'function') {
      window.invokeCSharpAction(text);
      return true;
    }
    if (typeof window.__bridgeSendShim === 'function') {
      window.__bridgeSendShim(text);
      return true;
    }
    return false;
  }

  function flush() {
    while (queue.length > 0) {
      if (!rawSend(queue[0])) { return; }
      queue.shift();
    }
    if (flushTimer !== null) {
      clearInterval(flushTimer);
      flushTimer = null;
    }
  }

  var handlers = [];

  window.bridge = {
    // page -> host (queued until the host-injected channel exists)
    send: function (obj) {
      var text = JSON.stringify(obj);
      if (queue.length === 0 && rawSend(text)) { return; }
      queue.push(text);
      if (flushTimer === null) { flushTimer = setInterval(flush, 50); }
    },
    // host -> page: .NET calls window.bridge.dispatch({type:'...', ...})
    onCommand: function (fn) { handlers.push(fn); },
    dispatch: function (cmd) {
      for (var i = 0; i < handlers.length; i++) { handlers[i](cmd); }
    }
  };

  // Criterion 5: route JS errors through the bridge into the .NET log.
  window.onerror = function (msg, src, line, col) {
    window.bridge.send({
      type: 'jsError', source: 'window.onerror',
      message: String(msg), where: String(src) + ':' + line + ':' + col
    });
  };
  window.addEventListener('unhandledrejection', function (e) {
    window.bridge.send({ type: 'jsError', source: 'unhandledrejection', message: String(e.reason) });
  });
  var origConsoleError = console.error.bind(console);
  console.error = function () {
    var args = Array.prototype.slice.call(arguments);
    try {
      window.bridge.send({ type: 'jsError', source: 'console.error', message: args.map(String).join(' ') });
    } catch (ignored) { /* never break the page over logging */ }
    origConsoleError.apply(null, args);
  };
})();
