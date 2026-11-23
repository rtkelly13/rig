// @ts-check
// A thin driver for `rig chrome-tabs`: it reports this profile's windows and tabs and carries out one step at a time.
// Every decision (what to close, what to group) is made in rig; nothing here plans.
//
// Chrome starts rig's native host (`rig chrome-tabs host`) when connectNative is called, one host per profile. The
// host relays requests from `rig chrome-tabs` commands over a local socket and this answers each one. The open port
// also keeps this service worker alive.

const HOST_NAME = "dev.ryankelly.rig_chrome_tabs";
const RECONNECT_MIN_MS = 1000;
const RECONNECT_MAX_MS = 5 * 60 * 1000;

/** @param {chrome.tabs.Tab} tab */
function tabRecord(tab) {
  return {
    id: tab.id,
    windowId: tab.windowId,
    index: tab.index,
    title: tab.title ?? "",
    url: tab.url ?? tab.pendingUrl ?? "",
    active: tab.active,
    pinned: tab.pinned,
    discarded: tab.discarded,
    audible: tab.audible ?? false,
    groupId: tab.groupId,
  };
}

async function snapshot() {
  const [windows, groups, focused] = await Promise.all([
    chrome.windows.getAll({ populate: true, windowTypes: ["normal"] }),
    chrome.tabGroups.query({}),
    chrome.windows.getLastFocused().catch(() => undefined),
  ]);
  return {
    focusedWindowId: focused?.focused ? focused.id : null,
    windows: windows.map((window) => ({
      id: window.id,
      state: window.state,
      incognito: window.incognito,
      focused: window.focused,
      left: window.left,
      top: window.top,
      width: window.width,
      height: window.height,
      tabs: (window.tabs ?? []).map(tabRecord),
    })),
    groups: groups.map((group) => ({ id: group.id, windowId: group.windowId, title: group.title ?? "", color: group.color, collapsed: group.collapsed })),
  };
}

async function profileEmail() {
  try {
    return (await chrome.identity.getProfileUserInfo({ accountStatus: chrome.identity.AccountStatus.ANY })).email;
  } catch {
    return "";
  }
}

/** @type {Record<string, (args: any) => Promise<unknown>>} */
const commands = {
  hello: async () => ({ email: await profileEmail(), version: chrome.runtime.getManifest().version }),
  windows: snapshot,
  close: async ({ tabId }) => {
    await chrome.tabs.remove(tabId);
    return {};
  },
  activate: async ({ tabId }) => tabRecord(await chrome.tabs.update(tabId, { active: true })),
  group: async ({ tabIds, groupId, windowId, title }) => {
    const id = groupId == null
      ? await chrome.tabs.group({ tabIds, createProperties: { windowId } })
      : await chrome.tabs.group({ tabIds, groupId });
    if (typeof title === "string") await chrome.tabGroups.update(id, { title });
    return { groupId: id };
  },
  ungroup: async ({ tabIds }) => {
    await chrome.tabs.ungroup(tabIds);
    return {};
  },
  move: async ({ tabId, windowId }) => {
    const moved = await chrome.tabs.move(tabId, { windowId, index: -1 });
    return tabRecord(Array.isArray(moved) ? moved[0] : moved);
  },
};

/** @param {{ id: string, cmd: string, args?: unknown }} request */
async function answer(request) {
  try {
    const run = commands[request.cmd];
    if (!run) throw new Error(`unknown command: ${request.cmd}`);
    return { type: "response", id: request.id, ok: true, result: await run(request.args ?? {}) };
  } catch (error) {
    return { type: "response", id: request.id, ok: false, error: String(error) };
  }
}

const RECONNECT_ALARM = "rig-reconnect";
/** Chrome's shortest alarm, which outlives the worker being stopped while no port keeps it alive. */
const ALARM_MIN_MINUTES = 0.5;

/** @type {chrome.runtime.Port | null} */
let port = null;
let delay = RECONNECT_MIN_MS;

/**
 * Tries again after `wait`. The timer keeps short retries quick; the alarm survives Chrome stopping this worker,
 * which it may do 30 s after the last port closed, and a new worker connects at start anyway.
 * @param {number} wait
 */
function retry(wait) {
  delay = Math.min(wait * 2, RECONNECT_MAX_MS);
  setTimeout(connect, wait);
  void chrome.alarms.create(RECONNECT_ALARM, { delayInMinutes: Math.max(ALARM_MIN_MINUTES, wait / 60000) });
}

function connect() {
  if (port) return;
  /** @type {chrome.runtime.Port} */
  let opened;
  try {
    opened = chrome.runtime.connectNative(HOST_NAME);
  } catch {
    retry(delay);
    return;
  }
  port = opened;
  let heard = false;
  opened.onMessage.addListener((message) => {
    if (!heard) {
      heard = true;
      delay = RECONNECT_MIN_MS;
      void chrome.alarms.clear(RECONNECT_ALARM);
    }
    if (message?.type === "request" && typeof message.id === "string" && typeof message.cmd === "string") {
      void answer(message).then((reply) => opened.postMessage(reply));
    }
  });
  opened.onDisconnect.addListener(() => {
    void chrome.runtime.lastError;
    if (port === opened) port = null;
    retry(heard ? RECONNECT_MIN_MS : delay);
  });
  void commands.hello({}).then((hello) => opened.postMessage({ type: "hello", ...hello }));
}

chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === RECONNECT_ALARM) connect();
});
chrome.runtime.onStartup.addListener(connect);
connect();
