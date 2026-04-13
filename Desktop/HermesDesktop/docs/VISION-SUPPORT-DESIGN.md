# Vision / Multimodal Image Support — Design

**Status:** Design only — not yet implemented.
**Trigger:** Feedback item #5 from v2.3.1 portable testing — users want to
upload or paste images into the chat for the agent to analyze with multimodal
models (Claude 3.5 Sonnet vision, GPT-4o vision, Qwen-VL, Llama 3.2 Vision,
etc.).
**Scope:** This document maps out the implementation across the existing
codebase so the work can be picked up in a follow-up branch with confidence.

---

## Why this is not a single-commit fix

The chat model in `src/Core/models.cs` currently holds messages as
`Message { string Content }` — a flat string. Every provider client
(`AnthropicClient`, `OpenAiClient`, `OllamaClient`, `QwenClient`, etc.)
serializes that string into its own wire format. Adding images means:

1. Extending the in-memory message model to carry attachments.
2. Updating every provider client's serializer to emit content blocks
   (Anthropic) or `image_url`/`input_image` (OpenAI) or whatever Ollama and
   Qwen want.
3. Updating the chat UI to accept images via paste / file picker / drag-drop.
4. Updating the chat bubble template to render attached images.
5. Persisting attachments in the transcript store so loaded sessions
   reproduce correctly.
6. Deciding image storage policy: temp files? `%LOCALAPPDATA%\hermes\images\`?
   Inline base64 in the transcript JSON? Each option has tradeoffs around
   disk usage, transcript portability, and provider request size.

That's ~15 file touches minimum and the kind of change that needs a real
build + test loop on Windows before shipping. Hence this design doc instead
of a half-finished implementation.

---

## Provider wire formats (the constraint)

### Anthropic (`AnthropicClient.cs`)

Anthropic uses content blocks. A user message with an image becomes:

```json
{
  "role": "user",
  "content": [
    { "type": "text", "text": "what's in this screenshot?" },
    {
      "type": "image",
      "source": {
        "type": "base64",
        "media_type": "image/png",
        "data": "<base64 bytes>"
      }
    }
  ]
}
```

Source type can also be `"url"` if we're sending a hosted URL, but the
feedback explicitly asked for base64, which keeps the agent self-contained
and avoids an upload-to-cloud step.

### OpenAI (`OpenAiClient.cs`)

OpenAI uses `image_url` with a data URI:

```json
{
  "role": "user",
  "content": [
    { "type": "text", "text": "what's in this screenshot?" },
    {
      "type": "image_url",
      "image_url": {
        "url": "data:image/png;base64,<base64 bytes>",
        "detail": "auto"
      }
    }
  ]
}
```

Note the difference: Anthropic separates `media_type` and `data`, OpenAI
folds them into a `data:image/png;base64,...` URI in a single string field.
A shared `ImageAttachment` type that exposes both `MediaType` and
`Base64Data` lets each serializer pick the form it needs without forcing the
core model to know about provider details.

### Ollama (multimodal models like `llama3.2-vision`)

Ollama's chat API accepts an `images` array on the message, with each image
as a raw base64 string (no data URI prefix, no `media_type`):

```json
{
  "role": "user",
  "content": "what's in this screenshot?",
  "images": ["<base64 bytes>"]
}
```

The shared model needs to hand each client the bytes in whatever envelope
the client wants.

### Qwen-VL / DeepSeek / others

Similar to OpenAI's `image_url` form. The same `ImageAttachment` shape works.

---

## Proposed model changes (`src/Core/models.cs`)

Add a new type and a new optional field on `Message`. Do NOT change
`Content` — that would break every provider client and every transcript on
disk.

```csharp
public sealed class ImageAttachment
{
    /// <summary>MIME type, e.g. "image/png" or "image/jpeg".</summary>
    public required string MediaType { get; init; }

    /// <summary>Raw base64 string (no "data:" prefix, no padding rules).</summary>
    public required string Base64Data { get; init; }

    /// <summary>Optional source filename for display in the UI / logs.</summary>
    public string? FileName { get; init; }

    /// <summary>Build a data URI string for OpenAI / Qwen / DeepSeek wire formats.</summary>
    public string ToDataUri() => $"data:{MediaType};base64,{Base64Data}";
}

public sealed class Message
{
    public required string Role { get; init; }
    public required string Content { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
    public List<ToolCall>? ToolCalls { get; init; }

    /// <summary>
    /// Optional images attached to a user message. Provider clients that
    /// support vision serialize these into content blocks; clients that
    /// don't simply ignore them (and the model never sees the image).
    /// </summary>
    public List<ImageAttachment>? Attachments { get; init; }
}
```

`Attachments` is `List<>?` so existing code that constructs `Message`
without attachments needs zero changes.

---

## Per-client serializer updates

Each provider client currently builds its request body from
`IEnumerable<Message>`. Find the loop that converts each `Message` to the
provider's JSON shape. Pseudo-template for all of them:

```csharp
foreach (var msg in messages)
{
    if (msg.Attachments is { Count: > 0 } && SupportsVision(currentModel))
    {
        // Emit a content-block array combining msg.Content and the images.
    }
    else
    {
        // Existing single-string content path — unchanged.
    }
}
```

Files to touch (find each one's `BuildRequest` / `Serialize` / equivalent
method):

- `src/LLM/AnthropicClient.cs` — emit `content` as an array of `text` and
  `image` blocks.
- `src/LLM/OpenAiClient.cs` — emit `content` as an array of `text` and
  `image_url` blocks (look for `openaiclient.cs`).
- `src/LLM/OllamaClient.cs` (if present) — emit `images` array alongside
  the existing string `content`.
- `src/LLM/QwenClient.cs` (if present) — same shape as OpenAI.
- Any other clients in `src/LLM/`.

Add a `SupportsVision(string modelId)` helper in each client (or in a
shared `ModelCatalog` / `IChatClient` extension). Models that don't support
vision should silently drop attachments — never throw, the user already
clicked send. The chat UI should warn ahead of time if the currently
selected model doesn't support images (use a small badge near the
attachment thumbnail).

---

## UI changes (`Desktop/HermesDesktop/Views/ChatPage.xaml(.cs)`)

### Input area

The chat input lives in `ChatPage.xaml` around line 112-145 (the "Input
Area — Claude Code style" StackPanel). Changes:

1. **Attach button**: add a small icon button inside the input border, to
   the left of the existing Send button. Glyph: paperclip (`\uE723` from
   Segoe MDL2 Assets) or image icon (`\uEB9F`). Click handler:
   `AttachImage_Click`.

2. **Paste handler**: subscribe to `PromptTextBox.Paste` event. Inspect
   `Clipboard.GetContent()` for `StandardDataFormats.Bitmap`. If present,
   read the bitmap, encode to PNG base64, create an `ImageAttachment`, and
   add to the pending-attachments list. Cancel the default paste so the
   image bytes don't end up as garbage text in the prompt box.

3. **Drag-drop handler**: subscribe `PromptTextBox.DragOver` and
   `PromptTextBox.Drop`. Accept files with image extensions, read them,
   base64-encode.

4. **Pending attachments strip**: a horizontal `ItemsRepeater` above the
   prompt textbox showing thumbnails of attachments staged for the next
   message, each with a small "x" button to remove. Bound to a
   `ObservableCollection<PendingAttachment>` field on `ChatPage`.

5. **Send**: when the user clicks Send, copy the pending attachments into
   the outgoing `Message`'s `Attachments` list, then clear the pending
   collection.

### Message bubbles

The chat bubble template in `ChatPage.xaml` lines 60-87 needs an
`ItemsControl` showing attachment thumbnails inside the bubble, above the
text content. Each thumbnail is an `<Image>` whose `Source` is a
`BitmapImage` constructed from the base64 bytes. Click to open
full-size in a flyout.

`ChatMessageItem` (in `Desktop/HermesDesktop/Models/`) needs an
`Attachments` collection synced from the `Message` it wraps.

---

## Storage policy

**Recommendation: inline base64 in the transcript JSON, with a soft
size cap.**

Pros: transcripts stay portable (single file, no external image directory
to keep in sync), session export/import works without extra steps, no
cleanup logic needed when sessions are deleted.

Cons: transcript files get bigger. A typical screenshot is ~100-500 KB
base64; ten screenshots in a session = ~5 MB JSON. Acceptable for normal
use; the soft cap below catches abuse.

**Soft cap:** reject any single attachment over **5 MB** decoded with a
toast "Image too large — please resize before sending". Reject the message
send if total attachments for the message exceed **20 MB** decoded. These
are arbitrary but generous defaults; expose them in `config.yaml` under a
new `vision:` section if users complain.

**Alternative (rejected):** writing images to
`%LOCALAPPDATA%\hermes\images\` with a content hash and storing only the
path in the transcript. Adds GC complexity (when do we delete orphaned
files?), breaks transcript portability across machines, and forces the
agent loop to read from disk on every turn that includes an image. The
inline approach trades disk size for simplicity and that's the right call
at this stage.

---

## Implementation order (to minimize broken intermediate states)

1. **Model**: add `ImageAttachment` and `Message.Attachments` to
   `src/Core/models.cs`. Compile is unaffected — every existing call site
   keeps working because the field is nullable.

2. **Persistence**: extend `src/transcript/transcriptstore.cs` to round-trip
   `Attachments` through JSON. Test by manually loading an old session
   (no attachments) and a new session (with attachments).

3. **One provider — `AnthropicClient`**: implement vision content blocks
   end-to-end against Claude Sonnet. This is the easiest provider to test
   (clean content-block API). Verify with a hand-built `Message` containing
   an attachment via a unit test or a `dotnet run` smoke test.

4. **UI input**: add the attach button and paste handler in `ChatPage`.
   Wire it to a `_pendingAttachments` collection. On Send, pass into the
   outgoing `Message`.

5. **UI bubble rendering**: add the attachment thumbnail row to the
   chat bubble template.

6. **Other providers**: `OpenAiClient`, `OllamaClient`, `QwenClient`. Each
   one is self-contained — no cross-file changes.

7. **`SupportsVision(modelId)` helper** in each client + a UI affordance
   that disables the attach button (or shows a tooltip) when the
   currently-selected model doesn't support vision.

8. **Soft size caps** in the input handler.

Each step compiles and runs on its own. Step 3 alone makes vision work
for Claude users; everything after step 3 broadens compatibility.

---

## Testing checklist (before merging the feature)

- [ ] Paste a screenshot into the chat input — thumbnail appears in the
      pending strip.
- [ ] Send a message with one attachment to Claude Sonnet — Claude
      describes the image accurately.
- [ ] Send to a non-vision model (e.g. Llama 3.1 8B text-only) — UI shows
      "model does not support vision" warning, attachment is dropped.
- [ ] Reload the session from disk — the message renders with the
      attachment intact.
- [ ] Drag-drop a `.png` and a `.jpg` from Explorer — both attach.
- [ ] Try a 6 MB image — rejected with toast.
- [ ] Try ten 1 MB images on one message — rejected at the 20 MB total.
- [ ] Run `publish-portable.ps1 -Zip`, smoke test the resulting
      `HermesDesktop-portable-x64.zip` against the same checklist.

---

## Out of scope for the first vision PR

- **Video/audio attachments** — different content blocks, different size
  envelope, different model support. Defer.
- **Generated images going *back* into the chat** — image-out from
  DALL-E 3 / Imagen / Stable Diffusion is a separate feature.
- **OCR fallback for non-vision models** — tempting (run images through a
  cheap OCR tool and inject text), but it changes semantics in a way users
  might not expect. Defer until someone explicitly asks.
- **Image editing / annotation in the chat UI** — adds a whole subsystem.
  If asked for, treat as a separate feature request.
