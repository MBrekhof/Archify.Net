(function () {
      var reader = document.querySelector('.container');
      var diagram = document.querySelector('.diagram-container');
      var svg = diagram && (
        diagram.querySelector(':scope > svg') ||
        diagram.querySelector(':scope > .diagram-stage > svg')
      );
      var stage = diagram && (diagram.querySelector(':scope > .diagram-stage') || svg);
      var legend = svg && svg.querySelector('[data-legend]');
      var navigationDock = diagram && diagram.querySelector('.diagram-nav');
      var viewBox = svg && svg.viewBox && svg.viewBox.baseVal;
      var diagramWidth = svg ? svg.getBoundingClientRect().width : 0;
      var viewBoxWidth = viewBox ? viewBox.width : 0;
      var scale = viewBoxWidth > 0 ? Math.min(1, diagramWidth / viewBoxWidth) : 0;
      var minimum = null;
      var minimumNonEdge = null;
      var minimumEdge = null;
      if (svg && scale > 0) {
        Array.from(svg.querySelectorAll('text[data-node-label], text[data-boundary-label], text[data-detail="context"], g[data-detail="context"] text')).forEach(function (text) {
          if (text.getAttribute('data-detail') === 'fine' || text.closest('[data-detail="fine"]')) return;
          var edge = text.closest('[data-edge-from][data-edge-to]');
          var node = text.closest('[data-node-id]');
          var context = text.getAttribute('data-detail') === 'context' || Boolean(text.closest('g[data-detail="context"]'));
          var detail = text.hasAttribute('data-node-label')
            ? 'primary'
            : text.hasAttribute('data-boundary-label') ? 'boundary'
              : context && edge ? 'edge' : 'context';
          if (detail === 'context' && !node) return;
          var sourceFontPx = parseFloat(text.getAttribute('font-size') || '');
          if (!Number.isFinite(sourceFontPx)) return;
          var projectedFontPx = sourceFontPx * scale;
          var entry = {
            text: (text.textContent || '').trim(),
            detail: detail,
            owner: edge ? {
              kind: 'edge',
              id: edge.getAttribute('data-edge-id'),
              from: edge.getAttribute('data-edge-from'),
              to: edge.getAttribute('data-edge-to')
            } : node ? { kind: 'node', id: node.getAttribute('data-node-id') }
              : detail === 'boundary' ? { kind: 'boundary', id: null } : null,
            sourceFontPx: sourceFontPx,
            projectedFontPx: projectedFontPx
          };
          if (!minimum || projectedFontPx < minimum.projectedFontPx) minimum = entry;
          if (detail === 'edge') {
            if (!minimumEdge || projectedFontPx < minimumEdge.projectedFontPx) minimumEdge = entry;
          } else if (!minimumNonEdge || projectedFontPx < minimumNonEdge.projectedFontPx) {
            minimumNonEdge = entry;
          }
        });
      }
      function intersectionArea(a, b) {
        if (!a || !b || !a.width || !a.height || !b.width || !b.height) return 0;
        var width = Math.max(0, Math.min(a.right, b.right) - Math.max(a.left, b.left));
        var height = Math.max(0, Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top));
        return width * height;
      }
      var legendRect = legend ? legend.getBoundingClientRect() : null;
      var stageRect = window.Archify && Archify.viewerChromeLayout
        && typeof Archify.viewerChromeLayout.stageRect === 'function'
        ? Archify.viewerChromeLayout.stageRect()
        : (stage ? stage.getBoundingClientRect() : null);
      // The dock may lift to the viewport floor while the page scrolls; the
      // stage contract is about its resting position.
      var navigationDockRect = !navigationDock ? null
        : window.Archify && Archify.viewerChromeLayout && typeof Archify.viewerChromeLayout.dockRect === 'function'
          ? Archify.viewerChromeLayout.dockRect()
          : navigationDock.getBoundingClientRect();
      var stageDockIntersectionArea = intersectionArea(stageRect, navigationDockRect);
      var viewerChromeReceipt = window.Archify && Archify.viewerChromeLayout
        && typeof Archify.viewerChromeLayout.receipt === 'function'
        ? Archify.viewerChromeLayout.receipt()
        : null;
      // Read rendered boxes only; source ownership, offsets and hard pins are unknown.
      var workflowLanes = svg ? Array.from(svg.querySelectorAll('rect[data-composition-frame-kind="lane"]')).map(function (lane) {
        var rect = lane.getBoundingClientRect();
        var members = Array.from(svg.querySelectorAll('g[data-node-id]')).map(function (node) {
          var box = node.querySelector('rect');
          if (!box) return null;
          var bounds = box.getBoundingClientRect();
          if (bounds.top < rect.top - 1 || bounds.bottom > rect.bottom + 1
            || bounds.left < rect.left - 1 || bounds.right > rect.right + 1) return null;
          return { id: node.getAttribute('data-node-id'), top: bounds.top, bottom: bounds.bottom };
        }).filter(Boolean);
        var top = members.length ? members.reduce(function (minimum, node) { return Math.min(minimum, node.top); }, Infinity) : null;
        var bottom = members.length ? members.reduce(function (maximum, node) { return Math.max(maximum, node.bottom); }, -Infinity) : null;
        return {
          frameId: lane.getAttribute('data-composition-frame-id'),
          heightPx: Math.round(rect.height),
          nodeCount: members.length,
          nodeIds: members.slice(0, 12).map(function (node) { return node.id; }),
          nodeSpanPx: top === null ? null : Math.round(bottom - top),
          spaceAboveNodesPx: top === null ? null : Math.round(top - rect.top),
          spaceBelowNodesPx: bottom === null ? null : Math.round(rect.bottom - bottom)
        };
      }).sort(function (a, b) { return b.heightPx - a.heightPx; }).slice(0, 6) : [];
      // Vertical page budget: every block that stacks above or below the SVG,
      // so an overflow receipt can say which part must give up how many pixels.
      function outerHeight(element) {
        if (!element) return 0;
        var style = window.getComputedStyle(element);
        if (element.hidden || style.display === 'none') return 0;
        return element.getBoundingClientRect().height
          + (parseFloat(style.marginTop) || 0) + (parseFloat(style.marginBottom) || 0);
      }
      var bodyStyle = window.getComputedStyle(document.body);
      var diagramStyle = diagram ? window.getComputedStyle(diagram) : null;
      var svgHeight = svg ? svg.getBoundingClientRect().height : 0;
      var notesBelowFold = Boolean(reader) && document.documentElement.getAttribute('data-reader-rail') === 'bottom';
      var pageComposition = {
        bodyPaddingPx: Math.round((parseFloat(bodyStyle.paddingTop) || 0) + (parseFloat(bodyStyle.paddingBottom) || 0)),
        headerPx: Math.round(outerHeight(reader && reader.querySelector('.header'))),
        diagramChromePx: Math.round(outerHeight(diagram) - svgHeight),
        svgPx: Math.round(svgHeight),
        cardsPx: notesBelowFold ? 0 : Math.round(outerHeight(reader && reader.querySelector('.cards'))),
        viewBoxHeight: viewBox ? viewBox.height : 0
      };
      var svgRect = svg && svg.getBoundingClientRect();
      var diagramRect = diagram && diagram.getBoundingClientRect();
      var documentScrollUnclipped = Boolean(svgRect && diagramRect
        && svgRect.top >= diagramRect.top - 1 && svgRect.left >= diagramRect.left - 1
        && svgRect.bottom <= diagramRect.bottom + 1 && svgRect.right <= diagramRect.right + 1
        && diagram.scrollHeight <= diagram.clientHeight + 1
        && diagram.scrollWidth <= diagram.clientWidth + 1
        && [document.documentElement, document.body].every(function (element) {
          return !['hidden', 'clip'].includes(window.getComputedStyle(element).overflowY);
        }));
      return {
        pageComposition: pageComposition,
        innerWidth: window.innerWidth,
        innerHeight: window.innerHeight,
        scrollWidth: Math.ceil(document.documentElement.scrollWidth),
        // Bottom notes and index are reading material below the fold; the
        // Reader excludes them from the one-screen fit, and so does this check.
        scrollHeight: Math.max(window.innerHeight, Math.ceil(document.documentElement.scrollHeight - (
          notesBelowFold ? outerHeight(reader.querySelector('.reader-rail')) : 0))),
        resolvedTheme: document.documentElement.getAttribute('data-theme') || '',
        readerLayout: document.documentElement.getAttribute('data-reader-layout') || null,
        readerOverflow: document.documentElement.getAttribute('data-reader-overflow') || null,
        readerFit: svg ? svg.getAttribute('data-reader-fit') : null,
        diagramType: svg ? svg.getAttribute('data-diagram-type') : null,
        documentScrollUnclipped: documentScrollUnclipped,
        readerWidth: reader ? reader.getBoundingClientRect().width : 0,
        diagramWidth: diagramWidth,
        viewBoxWidth: viewBoxWidth,
        workflowLanes: workflowLanes,
        minimumProjectedNodeTextPx: minimum ? minimum.projectedFontPx : null,
        minimumProjectedNonEdgeTextPx: minimumNonEdge ? minimumNonEdge.projectedFontPx : null,
        minimumProjectedEdgeTextPx: minimumEdge ? minimumEdge.projectedFontPx : null,
        minimumProjectedNodeText: minimum ? minimum.text : null,
        minimumProjectedNodeTextDetail: minimum ? minimum.detail : null,
        minimumProjectedNodeTextOwner: minimum ? minimum.owner : null,
        hasLegend: Boolean(legendRect && legendRect.width && legendRect.height),
        hasNavigationDock: Boolean(navigationDockRect && navigationDockRect.width && navigationDockRect.height),
        legendDockIntersectionArea: stageDockIntersectionArea > 0
          ? intersectionArea(legendRect, navigationDockRect)
          : 0,
        dockStageIntersectionArea: stageDockIntersectionArea,
        dockStageGap: stageRect && navigationDockRect ? navigationDockRect.top - stageRect.bottom : null,
        viewerChromeRequiredGap: viewerChromeReceipt ? viewerChromeReceipt.gap : null,
        viewerChromeReserve: viewerChromeReceipt ? viewerChromeReceipt.reserve : 0,
        viewerChromeActive: viewerChromeReceipt ? viewerChromeReceipt.active : false
      };
    })()