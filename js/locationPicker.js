export function showDialog(dialog) {
  dialog?.showModal();
}

export function closeDialog(dialog) {
  if (dialog?.open) dialog.close();
}

let activeMap = null;

export function initializeMap(svg, viewport, tooltip) {
  disposeMap();

  const controller = new AbortController();
  const view = { x: 0, y: 0, width: 1000, height: 332 };
  let focus = { x: 0.17, y: 0.5 };
  let drag = null;
  let suppressClick = false;

  const clamp = (value, min, max) => Math.max(min, Math.min(max, value));

  function renderView() {
    svg.setAttribute(
      "viewBox",
      `${view.x} ${view.y} ${view.width} ${view.height}`,
    );
    // Counter-scale the dots so their visible size stays steady while the map zooms.
    svg.style.setProperty("--marker-scale", view.width / 1000);
    viewport.classList.toggle("is-zoomed", view.width < 1000);
  }

  function hideTooltip() {
    tooltip.hidden = true;
  }

  function showTooltip(marker, event) {
    const name = marker?.dataset.locationName;
    if (!name) return hideTooltip();

    tooltip.textContent = name;
    tooltip.hidden = false;
    const bounds = viewport.getBoundingClientRect();
    const x = event.clientX - bounds.left;
    const y = event.clientY - bounds.top;
    tooltip.style.left = `${clamp(x + 12, 8, viewport.clientWidth - tooltip.offsetWidth - 8)}px`;
    tooltip.style.top = `${clamp(y + 12, 8, viewport.clientHeight - tooltip.offsetHeight - 8)}px`;
  }

  function zoomBy(factor, event) {
    const currentZoom = 1000 / view.width;
    const nextZoom = clamp(currentZoom * factor, 1, 20);
    if (nextZoom === currentZoom) return false;

    if (event) {
      const bounds = svg.getBoundingClientRect();
      focus = {
        x: clamp((event.clientX - bounds.left) / bounds.width, 0, 1),
        y: clamp((event.clientY - bounds.top) / bounds.height, 0, 1),
      };
    }

    const width = 1000 / nextZoom;
    const height = 332 / nextZoom;
    view.x = clamp(view.x + focus.x * (view.width - width), 0, 1000 - width);
    view.y = clamp(view.y + focus.y * (view.height - height), 0, 332 - height);
    view.width = width;
    view.height = height;
    renderView();
    hideTooltip();
    return true;
  }

  function onPointerMove(event) {
    if (drag?.id === event.pointerId) {
      const dx = event.clientX - drag.clientX;
      const dy = event.clientY - drag.clientY;
      if (Math.abs(dx) > 3 || Math.abs(dy) > 3) drag.moved = true;
      if (drag.moved) {
        const bounds = svg.getBoundingClientRect();
        view.x = clamp(
          drag.x - (dx * view.width) / bounds.width,
          0,
          1000 - view.width,
        );
        view.y = clamp(
          drag.y - (dy * view.height) / bounds.height,
          0,
          332 - view.height,
        );
        renderView();
        hideTooltip();
      }
      return;
    }

    const bounds = svg.getBoundingClientRect();
    focus = {
      x: clamp((event.clientX - bounds.left) / bounds.width, 0, 1),
      y: clamp((event.clientY - bounds.top) / bounds.height, 0, 1),
    };
    showTooltip(event.target.closest?.(".location-picker-dot"), event);
  }

  function onPointerDown(event) {
    if (view.width === 1000 || event.button !== 0 || !event.isPrimary) return;
    drag = {
      id: event.pointerId,
      clientX: event.clientX,
      clientY: event.clientY,
      x: view.x,
      y: view.y,
      moved: false,
      target: event.target,
    };
    event.target.setPointerCapture(event.pointerId);
    viewport.classList.add("is-dragging");
  }

  function onPointerUp(event) {
    if (drag?.id !== event.pointerId) return;
    if (drag.target.hasPointerCapture(event.pointerId))
      drag.target.releasePointerCapture(event.pointerId);
    if (drag.moved) {
      suppressClick = true;
      setTimeout(() => {
        suppressClick = false;
      }, 0);
    }
    drag = null;
    viewport.classList.remove("is-dragging");
  }

  svg.addEventListener(
    "wheel",
    (event) => {
      if (zoomBy(event.deltaY < 0 ? 1.25 : 0.8, event)) event.preventDefault();
    },
    { passive: false, signal: controller.signal },
  );
  svg.addEventListener(
    "pointerover",
    (event) => {
      showTooltip(event.target.closest?.(".location-picker-dot"), event);
    },
    { signal: controller.signal },
  );
  svg.addEventListener("pointermove", onPointerMove, {
    signal: controller.signal,
  });
  svg.addEventListener("pointerleave", hideTooltip, {
    signal: controller.signal,
  });
  svg.addEventListener("pointerdown", onPointerDown, {
    signal: controller.signal,
  });
  svg.addEventListener("pointerup", onPointerUp, { signal: controller.signal });
  svg.addEventListener("pointercancel", onPointerUp, {
    signal: controller.signal,
  });
  svg.addEventListener(
    "click",
    (event) => {
      if (!suppressClick) return;
      event.preventDefault();
      event.stopImmediatePropagation();
      suppressClick = false;
    },
    { capture: true, signal: controller.signal },
  );

  renderView();
  hideTooltip();
  activeMap = {
    zoomBy,
    dispose: () => {
      controller.abort();
      viewport.classList.remove("is-zoomed", "is-dragging");
      hideTooltip();
    },
  };
}

export function zoomMap(factor) {
  activeMap?.zoomBy(factor);
}

export function disposeMap() {
  activeMap?.dispose();
  activeMap = null;
}
