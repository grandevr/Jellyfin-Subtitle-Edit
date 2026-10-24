'use strict';
// Loads the logic under test. The mutation check sets SQE_LOGIC_PATH to a broken copy; normally it is ../logic.js.
module.exports = require(process.env.SQE_LOGIC_PATH || '../logic.js');
