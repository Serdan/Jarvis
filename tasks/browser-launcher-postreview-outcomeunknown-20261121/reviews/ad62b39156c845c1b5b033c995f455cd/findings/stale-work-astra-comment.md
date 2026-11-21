# Low-severity stale Work-model comment (nonblocking)

**Source:** frozen commit `1fabb066cc51ca0ca5d22237d7f858df1e68c7a9`, `browser-scripts/chatgpt/launchAgent.js:87`.

The comment says “The visible Work GPT-6.2 model is Astra; its default thinking effort is Medium” while the immediately following preference selects `GPT-6.2 Sol` at Medium (line 90), and the Work picker guard checks `gpt-6.2-sol-wm` (line 237). The corresponding fixtures and launcher docs also select Sol.

**Impact:** misleading maintenance guidance only. Runtime code and tested fallback do not select Astra. No acceptance-blocking behavior was reproduced.

**Remedy candidate:** Update/remove the stale comment in a separately authorized small source correction. This is not a demand to expand the original task or a reason to reject the narrow Sol correction.