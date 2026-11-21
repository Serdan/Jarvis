"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const {makeIntent, dispatchOnce} = require("./fixtures/trusted-send-prototype.cjs");

function fixture(overrides = {}) {
  const intent = makeIntent({operationId:"fixture-only-1",tabId:22,
    registrationGeneration:3,promptDigest:"sha256:sample-prompt",
    selectionDigest:"chat-gpt6-high"});
  let clock = 1000, pressed = 0, released = 0;
  const calls = [];
  let probeCount = 0, tabCount = 0, authorizationCount = 0;
  const tab = {id:22,windowId:4,url:"https://chatgpt.com/",
    pendingUrl:null,status:"complete",discarded:false,frozen:false};
  const page = {href:"https://chatgpt.com/", visibilityState:"visible",
    hasFocus:true, documentReady:"complete", promptDigest:intent.promptDigest,
    selectionDigest:intent.selectionDigest, sendCount:1,sendEnabled:true,
    hitTestIsSend:true,sampledAt:clock,x:80,y:40,viewportWidth:640,
    viewportHeight:480,documentToken:"doc1",buttonToken:"button1"};
  const hooks = {
    intent,windowId:4,permitTrustedInput:true,syntheticCanaryFailed:true,
    syntheticClickAttempted:false,now:()=>clock,
    getTab:async()=>{tabCount++;return tab;},
    authorize:async()=>{authorizationCount++;return true;},
    probe:async()=>{probeCount++;return {...page,sampledAt:clock};},
    sendCommand:async(_target,command,params)=>{
      calls.push({command,params});
      if(params.type==="mousePressed") pressed++;
      if(params.type==="mouseReleased") released++;
    },
    ...overrides
  };
  return {hooks,intent,tab,page,calls,get pressed(){return pressed;},
    get released(){return released;},
    get probeCount(){return probeCount;},
    get tabCount(){return tabCount;},
    get authorizationCount(){return authorizationCount;},
    setClock:t=>clock=t};
}
test("isolated authorized intent dispatches exactly one mouse down and up; never claims Send",async()=>{
  const f=fixture(); const r=await dispatchOnce(f.hooks);
  assert.equal(r.status,"inputDispatched"); assert.equal(r.providerSubmitted,false);
  assert.equal(f.pressed,1);assert.equal(f.released,1);
  assert.deepEqual(f.calls.map(c=>c.params.type),
    ["mouseMoved","mousePressed","mouseReleased"]);
  assert.equal(f.intent.state,"InputDispatched");
  const second=await dispatchOnce(f.hooks);
  assert.equal(second.reason,"IntentAlreadyConsumed");
  assert.equal(f.pressed,1);
});
test("trusted transport off by default, even when source is executable",async()=>{
  const f=fixture({permitTrustedInput:false});
  assert.equal((await dispatchOnce(f.hooks)).status,"notAttempted");
  assert.equal(f.calls.length,0);
  assert.equal((await dispatchOnce(f.hooks)).reason,"IntentAlreadyConsumed");
});
test("separate synthetic canary failure must be established before selection",async()=>{
  const f=fixture({syntheticCanaryFailed:false});
  assert.equal((await dispatchOnce(f.hooks)).reason,"TrustedTransportNotAuthorized");
  assert.equal(f.calls.length,0);
});
test("never run CDP after synthetic click on SAME intent",async()=>{
  const f=fixture({syntheticClickAttempted:true});
  assert.equal((await dispatchOnce(f.hooks)).reason,"TrustedTransportNotAuthorized");
  assert.equal(f.pressed,0);
});
test("stale registration window/route is rejected before input",async()=>{
  for (const change of [t=>{t.id=23;},t=>{t.windowId=9;},
    t=>{t.url="https://chatgpt.com/c/somewhere";},
    t=>{t.pendingUrl="https://chatgpt.com/c/next";},
    t=>{t.frozen=true;},t=>{t.status="loading";}]) {
    const f=fixture();change(f.tab);
    assert.equal((await dispatchOnce(f.hooks)).reason,"TabMismatch");
    assert.equal(f.calls.length,0);
  }
});
test("unfocused page and wrong prompt/model/button cannot dispatch",async()=>{
  for (const change of [p=>{p.hasFocus=false;},p=>{p.visibilityState="hidden";},
    p=>{p.promptDigest="unrelated";},p=>{p.selectionDigest="work-other";},
    p=>{p.sendCount=2;},p=>{p.hitTestIsSend=false;},
    p=>{p.sendEnabled=false;},p=>{p.x=900;}]) {
    const f=fixture();change(f.page);
    assert.equal((await dispatchOnce(f.hooks)).reason,"ComposerOrTargetMismatch");
    assert.equal(f.pressed,0);
  }
});
test("revoked permission cannot dispatch",async()=>{
  const f=fixture({authorize:async()=>false});
  assert.equal((await dispatchOnce(f.hooks)).reason,"RevokedBeforeInput");
  assert.equal(f.calls.length,0);
});
test("mutation after pointer move blocks press, no synthetic retry",async()=>{
  const f=fixture();
  let probes=0;f.hooks.probe=async()=>probes++===0?{...f.page}:{...f.page,buttonToken:"replaced"};
  assert.equal((await dispatchOnce(f.hooks)).reason,"ChangedTargetBeforePress");
  assert.equal(f.pressed,0);assert.equal(f.released,0);
});
test("route navigation after mouseMoved blocks press",async()=>{
  const f=fixture();
  f.hooks.sendCommand=async(_target,_method,params)=>{
    f.calls.push(params); if(params.type==="mouseMoved") f.tab.url="https://chatgpt.com/c/new";
  };
  assert.equal((await dispatchOnce(f.hooks)).reason,"NavigationBeforePress");
  assert.deepEqual(f.calls.map(x=>x.type),["mouseMoved"]);
});
test("timeout/error on mousePressed is permanently uncertain, not retryable",async()=>{
  const f=fixture();
  f.hooks.sendCommand=async(_target,_method,params)=>{
    f.calls.push(params);
    if(params.type==="mousePressed") throw Error("synthetic CDP transport timeout");
  };
  assert.equal((await dispatchOnce(f.hooks)).status,"outcomeUnknown");
  assert.equal(f.intent.state,"OutcomeUnknown");
  assert.equal((await dispatchOnce(f.hooks)).reason,"IntentAlreadyConsumed");
  assert.deepEqual(f.calls.map(x=>x.type),["mouseMoved","mousePressed"]);
});
test("mouseReleased timeout preserves outcome uncertain, never fires a second release",async()=>{
  const f=fixture();
  f.hooks.sendCommand=async(_target,_method,params)=>{
    f.calls.push(params);if(params.type==="mouseReleased") throw Error("timeout");
  };
  assert.equal((await dispatchOnce(f.hooks)).status,"outcomeUnknown");
  assert.equal(f.intent.state,"OutcomeUnknown");
  assert.deepEqual(f.calls.map(x=>x.type),
    ["mouseMoved","mousePressed","mouseReleased"]);
});
test("post-press navigation holds release and reports unknown",async()=>{
  const f=fixture();
  f.hooks.sendCommand=async(_target,_method,params)=>{
    f.calls.push(params);if(params.type==="mousePressed") f.tab.url="https://chatgpt.com/c/new";
  };
  assert.equal((await dispatchOnce(f.hooks)).reason,"TargetChangedAfterPress");
  assert.equal(f.intent.state,"OutcomeUnknown");
  assert.deepEqual(f.calls.map(x=>x.type),["mouseMoved","mousePressed"]);
});
test("even a completed CDP dispatch never proves backend confirmation",async()=>{
  const f=fixture();
  const r=await dispatchOnce(f.hooks);
  assert.equal(r.status,"inputDispatched");
  assert.equal(r.providerSubmitted,false);
  assert.equal(r.evidence.includes("conversationRoute"),false);
});

test("stale probe fails without mouse input",async()=>{
  const f=fixture();
  f.hooks.probe=async()=>({...f.page,sampledAt:0});
  assert.equal((await dispatchOnce(f.hooks)).reason,"ComposerOrTargetMismatch");
  assert.equal(f.pressed,0);
});
test("permission revoked after pointer move blocks mousePressed",async()=>{
  const f=fixture();let checks=0;
  f.hooks.authorize=async()=>++checks<2;
  assert.equal((await dispatchOnce(f.hooks)).reason,"RevokedBeforePress");
  assert.deepEqual(f.calls.map(x=>x.params.type),["mouseMoved"]);
});
test("concurrent invocations sharing the same intent cannot click twice",async()=>{
  const f=fixture();
  const results=await Promise.all([dispatchOnce(f.hooks),dispatchOnce(f.hooks)]);
  assert.equal(results.filter(r=>r.status==="inputDispatched").length,1);
  assert.equal(results.filter(r=>r.reason==="IntentAlreadyConsumed").length,1);
  assert.equal(f.pressed,1);assert.equal(f.released,1);
});
test("two distinct background tabs in one window have independent one-shot transport",async()=>{
  const first=fixture(),second=fixture();
  second.intent.tabId=23;second.tab.id=23;
  second.intent.operationId="different-fixture";
  const results=await Promise.all([dispatchOnce(first.hooks),dispatchOnce(second.hooks)]);
  assert.deepEqual(results.map(r=>r.status),["inputDispatched","inputDispatched"]);
  assert.equal(first.pressed,1);assert.equal(second.pressed,1);
  assert.deepEqual(first.calls.map(c=>c.command),Array(3).fill("Input.dispatchMouseEvent"));
});
