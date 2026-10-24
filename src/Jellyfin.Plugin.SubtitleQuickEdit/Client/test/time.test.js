'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const L = require('./load');

test('parseTime accepts hh:mm:ss,mmm', () => {
    assert.equal(L.parseTime('00:00:00,000'), 0);
    assert.equal(L.parseTime('00:00:01,500'), 1500);
    assert.equal(L.parseTime('00:00:59,999'), 59999);
    assert.equal(L.parseTime('00:01:00,000'), 60000);
    assert.equal(L.parseTime('01:02:03,004'), 3723004);
    assert.equal(L.parseTime('99:59:59,999'), 359999999);
});

test('parseTime accepts a dot for the comma, one-digit hours and surrounding spaces', () => {
    assert.equal(L.parseTime('00:00:01.250'), 1250);
    assert.equal(L.parseTime('1:02:03,004'), 3723004);
    assert.equal(L.parseTime('  00:00:02,000  '), 2000);
});

test('parseTime rejects bad input with null', () => {
    const bad = [
        '', ' ', 'abc', '00:00:01', '00:01', '1:2:3,4', '00:00:01,5', '00:00:01,50', '00:00:01,5000',
        '00:60:00,000', '00:00:60,000', '100:00:00,000', '-00:00:01,000', '00:00:01;000', '00:00:01 000',
        '00 : 00 : 01,000', '0x:00:01,000', '00:00:01,000 --> 00:00:02,000',
        null, undefined, 123, 1.5, {}, [], true, NaN,
    ];
    bad.forEach((value) => assert.equal(L.parseTime(value), null, 'should reject ' + JSON.stringify(value)));
});

test('formatTime writes hh:mm:ss,mmm', () => {
    assert.equal(L.formatTime(0), '00:00:00,000');
    assert.equal(L.formatTime(7), '00:00:00,007');
    assert.equal(L.formatTime(1500), '00:00:01,500');
    assert.equal(L.formatTime(59999), '00:00:59,999');
    assert.equal(L.formatTime(60000), '00:01:00,000');
    assert.equal(L.formatTime(3723004), '01:02:03,004');
    assert.equal(L.formatTime(359999999), '99:59:59,999');
});

test('formatTime rounds to whole milliseconds', () => {
    assert.equal(L.formatTime(1.6), '00:00:00,002');
    assert.equal(L.formatTime(1.4), '00:00:00,001');
    assert.equal(L.formatTime(999.5), '00:00:01,000');
});

test('formatTime returns null for values that are not a time', () => {
    [-1, -0.6, 360000000, NaN, Infinity, -Infinity, '5', null, undefined, {}, [], true].forEach((value) => {
        assert.equal(L.formatTime(value), null, 'should reject ' + String(value));
    });
});

test('format and parse are inverse for a spread of values', () => {
    [0, 1, 999, 1000, 59999, 60000, 3599999, 3600000, 3723004, 86399999, 359999999].forEach((ms) => {
        assert.equal(L.parseTime(L.formatTime(ms)), ms);
    });
});

test('secondsToMs converts video time to whole milliseconds', () => {
    assert.equal(L.secondsToMs(0), 0);
    assert.equal(L.secondsToMs(1.2345), 1235);
    assert.equal(L.secondsToMs(754.2), 754200);
    [-1, NaN, Infinity, '1', null, undefined].forEach((v) => assert.equal(L.secondsToMs(v), null));
});

test('nudgeTime moves by the delta and clamps at 0 and at the limit', () => {
    assert.equal(L.nudgeTime(1000, 100), 1100);
    assert.equal(L.nudgeTime(1000, -100), 900);
    assert.equal(L.nudgeTime(50, -100), 0);
    assert.equal(L.nudgeTime(59950, 100, 60000), 60000);
    assert.equal(L.nudgeTime(359999950, 100), 359999999);
    assert.equal(L.nudgeTime(1000, 100, 5000), 1100);
});
