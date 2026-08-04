// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

// Causality Flow view measurement interop. Blazor calls measure() after render to obtain the
// rectangle of every element carrying a data-flow-id (relative to the canvas), from which it
// computes the SVG connector overlay in C#. observeResize() registers a debounced ResizeObserver
// on the canvas element that calls back into the component so the overlay tracks reflowed card
// positions; observing the canvas rather than the window also catches reflows that fire no window
// resize event (fonts swapping in, a scrollbar appearing, the nav drawer toggling).
// unobserveResize() disconnects it on component disposal.
window.jimCausality = {
    _resizeStates: {},

    measure: function (canvasId) {
        const canvas = document.getElementById(canvasId);
        if (!canvas) {
            return null;
        }
        const canvasRect = canvas.getBoundingClientRect();
        const cards = [];
        canvas.querySelectorAll('[data-flow-id]').forEach(function (element) {
            const rect = element.getBoundingClientRect();
            // The header row a connector anchors on. querySelector takes the first in document order,
            // which for a Connected System group is the group's own header rather than the header of
            // the first event card inside it. Absent for anything that has no header row, in which
            // case C# falls back to the capped card centre.
            const head = element.querySelector('[data-flow-head]');
            const headRect = head ? head.getBoundingClientRect() : null;
            cards.push({
                id: element.getAttribute('data-flow-id'),
                left: rect.left - canvasRect.left,
                right: rect.right - canvasRect.left,
                top: rect.top - canvasRect.top,
                height: rect.height,
                headerTop: headRect ? headRect.top - canvasRect.top : 0,
                headerHeight: headRect ? headRect.height : 0
            });
        });
        return { cards: cards };
    },

    observeResize: function (canvasId, dotNetRef) {
        const canvas = document.getElementById(canvasId);
        if (!canvas) {
            return;
        }
        const state = { timeoutId: null };
        state.observer = new ResizeObserver(function () {
            if (state.timeoutId) {
                clearTimeout(state.timeoutId);
            }
            state.timeoutId = setTimeout(function () {
                state.timeoutId = null;
                dotNetRef.invokeMethodAsync('OnFlowResizeAsync').catch(function () {
                    // The circuit is gone; disposal or page unload will disconnect the observer
                });
            }, 150);
        });
        state.observer.observe(canvas);
        this._resizeStates[canvasId] = state;
    },

    unobserveResize: function (canvasId) {
        const state = this._resizeStates[canvasId];
        if (state) {
            if (state.timeoutId) {
                clearTimeout(state.timeoutId);
            }
            state.observer.disconnect();
            delete this._resizeStates[canvasId];
        }
    }
};
