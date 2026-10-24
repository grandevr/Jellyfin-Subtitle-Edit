(function () {
    'use strict';
    // Served after logic.js in the same file (see ClientAssetsController), so window.SqeLogic is already defined.
    console.log('SubtitleQuickEdit client loaded' + (window.SqeLogic ? '' : ' (logic module missing)'));
}());
