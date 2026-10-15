(function () {
      document.documentElement.setAttribute('data-motion', 'still');
      var panel = document.querySelector('.diagram-container');
      if (panel) panel.setAttribute('data-detail-level', 'read');
      var fontsReady = document.fonts && document.fonts.ready
        ? document.fonts.ready.catch(function () {})
        : Promise.resolve();
      if (window.Archify && Archify.layoutStability && typeof Archify.layoutStability.whenStable === 'function') {
        return fontsReady.then(function () { return Archify.layoutStability.whenStable(); });
      }
      return fontsReady.then(function () {
        if (window.Archify && Archify.readerLayout && typeof Archify.readerLayout.whenStable === 'function') {
          return Archify.readerLayout.whenStable();
        }
      }).then(function () {
        if (window.Archify && Archify.viewerChromeLayout && typeof Archify.viewerChromeLayout.whenStable === 'function') {
          return Archify.viewerChromeLayout.whenStable();
        }
      }).then(function () {
        if (window.Archify && Archify.readerLayout && typeof Archify.readerLayout.whenStable === 'function') {
          return Archify.readerLayout.whenStable();
        }
      }).then(function () {
        if (window.Archify && Archify.viewerChromeLayout && typeof Archify.viewerChromeLayout.whenStable === 'function') {
          return Archify.viewerChromeLayout.whenStable();
        }
        return new Promise(function (resolve) {
          requestAnimationFrame(function () { requestAnimationFrame(resolve); });
        });
      });
    })()