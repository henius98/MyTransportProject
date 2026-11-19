(function (root) {
  function isGzipContentEncoding(headerValue) {
    if (!headerValue) {
      return false;
    }
    var encodings = headerValue.split(",");
    for (var i = 0; i < encodings.length; i++) {
      var encoding = encodings[i].trim().toLowerCase();
      if (encoding === "gzip" || encoding === "x-gzip") {
        return true;
      }
    }
    return false;
  }

  function getSubtleCrypto() {
    var cryptoObj = (typeof self !== "undefined" && self.crypto) || root.crypto;
    return cryptoObj && cryptoObj.subtle;
  }

  function hasWasmMagic(buffer) {
    if (!buffer || buffer.byteLength < 4) {
      return false;
    }
    var bytes = new Uint8Array(buffer, 0, 4);
    return (
      bytes[0] === 0x00 &&
      bytes[1] === 0x61 &&
      bytes[2] === 0x73 &&
      bytes[3] === 0x6d
    );
  }

  function canVerifyIntegrity(expectedIntegrity) {
    return (
      typeof expectedIntegrity === "string" &&
      expectedIntegrity.indexOf("sha256-") === 0 &&
      Boolean(getSubtleCrypto())
    );
  }

  function verifySha256(buffer, expectedIntegrity) {
    var subtle = getSubtleCrypto();
    if (!canVerifyIntegrity(expectedIntegrity) || !subtle) {
      return Promise.resolve(false);
    }
    return subtle.digest("SHA-256", buffer).then(function (digest) {
      var bytes = new Uint8Array(digest);
      var binary = "";
      for (var i = 0; i < bytes.length; i++) {
        binary += String.fromCharCode(bytes[i]);
      }
      return "sha256-" + btoa(binary) === expectedIntegrity;
    });
  }

  function decompressWasmResponse(response, expectedIntegrity) {
    var headers = new Headers(response.headers);
    headers.set("Content-Type", "application/wasm");
    headers.delete("Content-Length");
    headers.delete("Content-Encoding");
    var bodyStream = isGzipContentEncoding(response.headers.get("Content-Encoding"))
      ? response.body
      : response.body.pipeThrough(new DecompressionStream("gzip"));
    return new Response(bodyStream).arrayBuffer().then(function (buffer) {
      if (!hasWasmMagic(buffer)) {
        return null;
      }
      return verifySha256(buffer, expectedIntegrity).then(function (valid) {
        if (!valid) {
          return null;
        }
        return new Response(buffer, {
          status: response.status,
          statusText: response.statusText,
          headers: headers,
        });
      });
    });
  }

  function fetchDecompressedWasm(gzUrl, expectedIntegrity, fetchOptions) {
    if (
      typeof DecompressionStream !== "function" ||
      !canVerifyIntegrity(expectedIntegrity)
    ) {
      return Promise.resolve(null);
    }
    try {
      var request = fetchOptions ? fetch(gzUrl, fetchOptions) : fetch(gzUrl);
      return Promise.resolve(request)
        .then(function (response) {
          if (!response || !response.ok || !response.body) {
            return null;
          }
          return decompressWasmResponse(response, expectedIntegrity);
        })
        .catch(function () {
          return null;
        });
    } catch (_) {
      return Promise.resolve(null);
    }
  }

  root.wasmGzip = {
    isGzipContentEncoding: isGzipContentEncoding,
    canVerifyIntegrity: canVerifyIntegrity,
    verifySha256: verifySha256,
    decompressWasmResponse: decompressWasmResponse,
    fetchDecompressedWasm: fetchDecompressedWasm,
  };
})(typeof globalThis !== "undefined" ? globalThis : self);
