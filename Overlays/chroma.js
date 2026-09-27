// Green-screen removal for alert videos. The video plays (with its sound) but is drawn onto a canvas
// through a chroma key: pixels close to the key colour go see-through. WebGL when the browser has it
// (OBS and TikTok LIVE Studio do), else a slower canvas 2D version. The maths follows OBS's own
// Chroma Key filter: distance in the colour (Cb/Cr) plane, "strength" = similarity, "softness" =
// smoothness, plus a little spill removal so green edges go grey instead of glowing.
window.GDChroma = (function () {
  function hexToRgb(hex) {
    let h = String(hex || '').trim().replace('#', '');
    if (h.length === 3) h = h.split('').map(c => c + c).join('');
    if (!/^[0-9a-f]{6}$/i.test(h)) return null;
    const n = parseInt(h, 16);
    return [(n >> 16 & 255) / 255, (n >> 8 & 255) / 255, (n & 255) / 255];
  }
  function toHex(rgb) { return '#' + rgb.map(v => Math.round(v * 255).toString(16).padStart(2, '0')).join('').toUpperCase(); }
  function cbcr(r, g, b) { return [0.5 - 0.168736 * r - 0.331264 * g + 0.5 * b, 0.5 + 0.5 * r - 0.418688 * g - 0.081312 * b]; }

  // The average colour of the four corners of a frame: on a green-screen clip that's the screen.
  function cornerColor(source, w, h) {
    const c = document.createElement('canvas'), s = 64;
    c.width = s; c.height = s;
    const g = c.getContext('2d', { willReadFrequently: true });
    g.drawImage(source, 0, 0, w, h, 0, 0, s, s);
    const d = g.getImageData(0, 0, s, s).data, sum = [0, 0, 0];
    let n = 0;
    for (const [x0, y0] of [[0, 0], [s - 6, 0], [0, s - 6], [s - 6, s - 6]])
      for (let y = y0; y < y0 + 6; y++) for (let x = x0; x < x0 + 6; x++) {
        const i = (y * s + x) * 4;
        sum[0] += d[i]; sum[1] += d[i + 1]; sum[2] += d[i + 2]; n++;
      }
    return sum.map(v => v / n / 255);
  }

  const VS = 'attribute vec2 p; varying vec2 uv; void main() { uv = vec2(p.x * .5 + .5, .5 - p.y * .5); gl_Position = vec4(p, 0., 1.); }';
  const FS = [
    'precision mediump float;',
    'uniform sampler2D tex; uniform vec2 key; uniform vec2 px; uniform float sim, smoothv, spill;',
    'varying vec2 uv;',
    'vec2 cbcr(vec3 c) { return vec2(.5 - .168736 * c.r - .331264 * c.g + .5 * c.b, .5 + .5 * c.r - .418688 * c.g - .081312 * c.b); }',
    'float dist(vec2 at) { return distance(cbcr(texture2D(tex, at).rgb), key); }',
    'void main() {',
    '  vec4 c = texture2D(tex, uv);',
    // A small box filter (as OBS does) so noisy single pixels don't flicker in and out.
    '  vec2 a = vec2(px.x, px.y * .5), b = vec2(px.x * .5, -px.y);',
    '  float d = (2. * (dist(uv - a) + dist(uv + a) + dist(uv - b) + dist(uv + b)) + distance(cbcr(c.rgb), key)) / 9.;',
    '  float base = d - sim;',
    '  float alpha = pow(clamp(base / smoothv, 0., 1.), 1.5);',
    '  float sp = pow(clamp(base / spill, 0., 1.), 1.5);',
    '  float grey = dot(c.rgb, vec3(.2126, .7152, .0722));',
    '  vec3 rgb = mix(vec3(grey), c.rgb, sp);',
    '  gl_FragColor = vec4(rgb * alpha, alpha);',
    '}'].join('\n');

  function webgl(canvas) {
    const gl = canvas.getContext('webgl', { premultipliedAlpha: true, alpha: true }) || canvas.getContext('experimental-webgl');
    if (!gl) return null;
    const sh = (type, src) => { const s = gl.createShader(type); gl.shaderSource(s, src); gl.compileShader(s); if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s)); return s; };
    const prog = gl.createProgram();
    gl.attachShader(prog, sh(gl.VERTEX_SHADER, VS));
    gl.attachShader(prog, sh(gl.FRAGMENT_SHADER, FS));
    gl.linkProgram(prog);
    if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(prog));
    gl.useProgram(prog);
    const buf = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, buf);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]), gl.STATIC_DRAW);
    const loc = gl.getAttribLocation(prog, 'p');
    gl.enableVertexAttribArray(loc);
    gl.vertexAttribPointer(loc, 2, gl.FLOAT, false, 0, 0);
    const tex = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, tex);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    const u = n => gl.getUniformLocation(prog, n);
    return {
      kind: 'webgl',
      draw(video, k) {
        gl.viewport(0, 0, canvas.width, canvas.height);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, video);
        gl.uniform2f(u('key'), k.key[0], k.key[1]);
        gl.uniform2f(u('px'), 1 / canvas.width, 1 / canvas.height);
        gl.uniform1f(u('sim'), k.sim);
        gl.uniform1f(u('smoothv'), k.smooth);
        gl.uniform1f(u('spill'), k.spill);
        gl.clearColor(0, 0, 0, 0);
        gl.clear(gl.COLOR_BUFFER_BIT);
        gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
      },
    };
  }

  function canvas2d(canvas) {
    const g = canvas.getContext('2d', { willReadFrequently: true });
    return {
      kind: '2d',
      draw(video, k) {
        g.drawImage(video, 0, 0, canvas.width, canvas.height);
        const img = g.getImageData(0, 0, canvas.width, canvas.height), d = img.data;
        for (let i = 0; i < d.length; i += 4) {
          const r = d[i] / 255, gg = d[i + 1] / 255, b = d[i + 2] / 255, c = cbcr(r, gg, b);
          const base = Math.hypot(c[0] - k.key[0], c[1] - k.key[1]) - k.sim;
          const alpha = Math.pow(Math.min(1, Math.max(0, base / k.smooth)), 1.5);
          const sp = Math.pow(Math.min(1, Math.max(0, base / k.spill)), 1.5);
          const grey = .2126 * r + .7152 * gg + .0722 * b;
          d[i] = 255 * (grey + (r - grey) * sp);
          d[i + 1] = 255 * (grey + (gg - grey) * sp);
          d[i + 2] = 255 * (grey + (b - grey) * sp);
          d[i + 3] = 255 * alpha;
        }
        g.putImageData(img, 0, 0);
      },
    };
  }

  // Plays `video` onto `canvas` with the key colour removed, until the canvas leaves the page.
  // opts: { color: '#00FF00' or '' (from the corners), strength: 1-100, softness: 1-100, force2d }.
  // onFail(err) runs if the video can't be keyed (e.g. a web video from a site that doesn't allow it):
  // the caller then shows the plain video instead.
  function attach(video, canvas, opts, onFail) {
    opts = opts || {};
    const k = { sim: Math.max(1, opts.strength || 40) / 100, smooth: Math.max(1, opts.softness || 8) / 100, spill: 0.1, key: null };
    let renderer = null, started = false;
    function setKey(rgb) { k.key = cbcr(rgb[0], rgb[1], rgb[2]); canvas.dataset.key = toHex(rgb); }
    function frame() {
      if (!canvas.isConnected) return;
      try {
        if (video.readyState >= 2 && video.videoWidth) {
          if (!started) {
            started = true;
            // Keep big videos to 1920 px so the 2D fallback stays quick enough.
            const f = Math.min(1, 1920 / Math.max(video.videoWidth, video.videoHeight));
            canvas.width = Math.round(video.videoWidth * f); canvas.height = Math.round(video.videoHeight * f);
            const want = hexToRgb(opts.color);
            setKey(want || cornerColor(video, video.videoWidth, video.videoHeight));
            if (!opts.force2d) { try { renderer = webgl(canvas); } catch (e) { console.warn('WebGL chroma key failed, using canvas 2D', e); renderer = null; } }
            if (!renderer) renderer = canvas2d(canvas);
            canvas.dataset.renderer = renderer.kind;
          }
          renderer.draw(video, k);
        }
      } catch (e) {
        if (onFail) onFail(e);
        return;
      }
      if (video.requestVideoFrameCallback && renderer) video.requestVideoFrameCallback(frame);
      else requestAnimationFrame(frame);
    }
    // Poll with animation frames until the first frame is there, then draw once per new video frame.
    requestAnimationFrame(frame);
  }

  // The colour to remove, read from a video's corners (the "Pick from video" button in GiftDeck).
  function pickFromUrl(url) {
    return new Promise((resolve, reject) => {
      const v = document.createElement('video');
      v.muted = true; v.preload = 'auto'; v.src = url;
      const fail = () => reject(new Error('Could not read that video'));
      v.onerror = fail;
      v.onloadedmetadata = () => { v.currentTime = Math.min(0.2, (v.duration || 1) / 2); };
      v.onseeked = () => { try { resolve(toHex(cornerColor(v, v.videoWidth, v.videoHeight))); } catch (e) { reject(e); } };
      setTimeout(fail, 8000);
    });
  }

  return { attach, pickFromUrl, hexToRgb };
})();
