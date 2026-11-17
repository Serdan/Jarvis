'use strict';

const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(
  path.join(__dirname, '..', '..', 'browser-scripts', 'chatgpt', 'launchAgent.js'),
  'utf8'
);

function fakePage() {
  let touched = 0;
  const page = {
    location: { href: 'https://chatgpt.com/' },
    document: {
      querySelectorAll() {
        touched += 1;
        return [];
      }
    }
  };
  const execute = vm.runInNewContext(source + '\nexecute', page, {
    filename: 'browser-scripts/chatgpt/launchAgent.js'
  });
  return { execute, touches: () => touched };
}

test('page-facing launcher rejects unsupported private bootstrap arguments before any send', async () => {
  const inputs = [
    { prompt: 'ordinary instruction', credential: 'DO_NOT_RELAY_SECRET' },
    { prompt: 'ordinary instruction', bearer: 'DO_NOT_RELAY_SECRET' },
    { prompt: 'ordinary instruction', bootstrap: { key: 'DO_NOT_RELAY_SECRET' } },
    { prompt: 'ordinary instruction', sessionIdentity: 'self-declared' },
    { prompt: 'ordinary instruction', firstConnect: 'fake-callback' }
  ];
  for (const input of inputs) {
    const page = fakePage();
    const response = await page.execute(input);
    assert.equal(response.status, 'notSubmitted');
    assert.equal(response.reason, 'UnsupportedPrivateBootstrapArguments');
    assert.equal(response.safeToDisposeCreatedTab, true);
    assert.doesNotMatch(JSON.stringify(response), /DO_NOT_RELAY_SECRET|fake-callback/);
    // Only structural no-turn checks are allowed; neither composer nor send
    // should be inspected or clicked after private args are rejected.
    assert.equal(page.touches(), 2);
  }
});

test('page-facing launcher rejects null, array and arbitrary future flags', async () => {
  for (const input of [null, [], { prompt: 'x', productionBridgeReady: true }]) {
    const page = fakePage();
    const response = await page.execute(input);
    assert.equal(response.status, 'notSubmitted');
    assert.equal(response.reason, 'UnsupportedPrivateBootstrapArguments');
  }
});
