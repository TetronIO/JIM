// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

// Causality Flow view measurement interop. Blazor calls measure() after render to obtain the
// canvas size and the rectangle of every element carrying a data-flow-id (relative to the canvas),
// from which it computes the SVG connector overlay in C#. observeResize() registers a debounced
// window resize listener that calls back into the component so the overlay tracks reflowed card
// positions; unobserveResize() removes it on component disposal.
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
            cards.push({
                id: element.getAttribute('data-flow-id'),
                left: rect.left - canvasRect.left,
                right: rect.right - canvasRect.left,
                top: rect.top - canvasRect.top,
                height: rect.height
            });
        });
        return { width: canvasRect.width, height: canvasRect.height, cards: cards };
    },

    observeResize: function (canvasId, dotNetRef) {
        const state = { timeoutId: null };
        state.handler = function () {
            if (state.timeoutId) {
                clearTimeout(state.timeoutId);
            }
            state.timeoutId = setTimeout(function () {
                dotNetRef.invokeMethodAsync('OnFlowResizeAsync');
            }, 150);
        };
        window.addEventListener('resize', state.handler);
        this._resizeStates[canvasId] = state;
    },

    unobserveResize: function (canvasId) {
        const state = this._resizeStates[canvasId];
        if (state) {
            if (state.timeoutId) {
                clearTimeout(state.timeoutId);
            }
            window.removeEventListener('resize', state.handler);
            delete this._resizeStates[canvasId];
        }
    }
};
