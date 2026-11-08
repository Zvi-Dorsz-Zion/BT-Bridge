package com.btbridge.app

import android.content.Context
import android.graphics.Rect
import android.text.Editable
import android.text.InputType
import android.text.Selection
import android.view.KeyCharacterMap
import android.view.KeyEvent
import android.view.View
import android.view.ViewTreeObserver
import android.view.inputmethod.BaseInputConnection
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputConnection
import android.view.inputmethod.InputMethodManager
import androidx.annotation.RequiresApi
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.ui.Modifier
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
import kotlin.math.abs

/** Lets the screen reach the keyboard view (to show the phone keyboard again without a second tap). */
class RemoteKeysHolder { var view: RemoteKeyView? = null }

/**
 * The phone keyboard, typing on the computer. Unlike a text field, nothing is ever written back to the keyboard,
 * so it is never restarted: after a digit it stays on the numbers, Shift and suggestions keep working.
 * [onText] gets typed text, [onKey] key names ("back", "enter", "left"…) with modifiers.
 * [onHidden] is called once the phone keyboard was closed (Back, its own hide button, or another field took over).
 */
@Composable
fun RemoteKeyboard(
    modifier: Modifier,
    holder: RemoteKeysHolder,
    onText: (String) -> Unit,
    onKey: (String, List<String>) -> Unit,
    onHidden: () -> Unit,
) {
    AndroidView(
        factory = { RemoteKeyView(it).also { v -> holder.view = v } },
        modifier = modifier,
        update = { v -> v.onText = onText; v.onKey = onKey; v.onHidden = onHidden },
    )
    DisposableEffect(Unit) { onDispose { holder.view = null } }
}

class RemoteKeyView(ctx: Context) : View(ctx) {
    var onText: (String) -> Unit = {}
    var onKey: (String, List<String>) -> Unit = { _, _ -> }
    var onHidden: () -> Unit = {}

    /** The phone keyboard was seen open since this view appeared; its closing then counts as "hidden". */
    private var imeSeen = false
    private var gone = false
    /** Another text field (the browser's address bar) took the keyboard over: leaving must not close it. */
    private var handedOver = false
    private val imm: InputMethodManager? get() = context.service<InputMethodManager>()
    private val layoutListener = ViewTreeObserver.OnGlobalLayoutListener { checkIme() }

    init {
        isFocusable = true
        isFocusableInTouchMode = true
    }

    override fun onCheckIsTextEditor() = true

    override fun onCreateInputConnection(out: EditorInfo): InputConnection {
        // Plain text with suggestions; Enter is a new line (a key on the computer), never an "action" button.
        out.inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_MULTI_LINE
        out.imeOptions = EditorInfo.IME_ACTION_NONE or EditorInfo.IME_FLAG_NO_EXTRACT_UI or
            EditorInfo.IME_FLAG_NO_FULLSCREEN or EditorInfo.IME_FLAG_NO_ENTER_ACTION
        out.initialSelStart = 0
        out.initialSelEnd = 0
        return Conn()
    }

    /** Is the phone keyboard on screen now? */
    fun imeVisible(): Boolean {
        val insets = runCatching { ViewCompat.getRootWindowInsets(this)?.isVisible(WindowInsetsCompat.Type.ime()) }.getOrNull()
        if (insets == true) return true
        // Older Android (or no answer): the keyboard takes the bottom of the window.
        val root = rootView ?: return false
        if (root.height <= 0) return false
        val r = Rect()
        getWindowVisibleDisplayFrame(r)
        return root.height - r.bottom > root.height * 0.15f
    }

    fun show() {
        requestFocus()
        runCatching { imm?.showSoftInput(this, 0) }
    }

    private fun checkIme() {
        if (gone) return
        if (imeVisible()) imeSeen = true
        else if (imeSeen) { imeSeen = false; onHidden() }
    }

    override fun onAttachedToWindow() {
        super.onAttachedToWindow()
        viewTreeObserver.addOnGlobalLayoutListener(layoutListener)
        post { show() }
        // Some phones ignore the first request while the window is still settling.
        postDelayed({ if (!gone && !imeVisible()) show() }, 300)
    }

    override fun onDetachedFromWindow() {
        gone = true
        viewTreeObserver.removeOnGlobalLayoutListener(layoutListener)
        // Closed by the button or by leaving the screen: the phone keyboard goes away too.
        if (!handedOver) runCatching { imm?.hideSoftInputFromWindow(windowToken, 0) }
        super.onDetachedFromWindow()
    }

    override fun onFocusChanged(gainFocus: Boolean, direction: Int, previouslyFocusedRect: Rect?) {
        super.onFocusChanged(gainFocus, direction, previouslyFocusedRect)
        // Lost the focus while still on screen (when it is being removed, gone is already set by then).
        if (!gainFocus && !gone) post {
            if (gone) return@post
            val f = rootView?.findFocus()
            handedOver = f != null && f !== this && f.onCheckIsTextEditor()
            onHidden()
        }
    }

    private fun emit(s: String) {
        if (s.isEmpty()) return
        val lines = s.split('\n')
        lines.forEachIndexed { i, part ->
            if (part.isNotEmpty()) onText(part)
            if (i < lines.lastIndex) onKey("enter", emptyList())
        }
    }

    /**
     * A private copy of what was typed, for the keyboard's suggestions. After every change, what differs from what the
     * computer already has goes out: Backspaces for removed characters, then the new text. So composing (suggestions,
     * autocorrect, Hebrew) neither doubles nor loses letters. The computer's cursor is taken to be after our text.
     */
    private inner class Conn : BaseInputConnection(this@RemoteKeyView, true) {
        private val ed: Editable = Editable.Factory.getInstance().newEditable("")
        private var sent = ""
        private var batch = 0

        override fun getEditable(): Editable = ed

        private fun changed() {
            if (batch > 0) return
            val now = ed.toString()
            if (now != sent) {
                val n = minOf(now.length, sent.length)
                var p = 0
                while (p < n && now[p] == sent[p]) p++
                if (p > 0 && Character.isHighSurrogate(now[p - 1])) p--
                val removed = sent.codePointCount(p, sent.length)
                val added = now.substring(p)
                sent = now
                repeat(removed) { onKey("back", emptyList()) }
                emit(added)
            }
            report()
        }

        private fun report() {
            runCatching {
                imm?.updateSelection(this@RemoteKeyView, Selection.getSelectionStart(ed), Selection.getSelectionEnd(ed),
                    BaseInputConnection.getComposingSpanStart(ed), BaseInputConnection.getComposingSpanEnd(ed))
            }
        }

        override fun beginBatchEdit(): Boolean { batch++; return true }

        override fun endBatchEdit(): Boolean {
            if (batch > 0) batch--
            if (batch == 0) changed()
            return batch > 0
        }

        override fun commitText(text: CharSequence?, newCursorPosition: Int): Boolean {
            val r = super.commitText(text, newCursorPosition)
            changed()
            return r
        }

        override fun setComposingText(text: CharSequence?, newCursorPosition: Int): Boolean {
            val r = super.setComposingText(text, newCursorPosition)
            changed()
            return r
        }

        override fun setComposingRegion(start: Int, end: Int): Boolean {
            val r = super.setComposingRegion(start, end)
            changed()
            return r
        }

        override fun finishComposingText(): Boolean {
            val r = super.finishComposingText()
            changed()
            return r
        }

        override fun deleteSurroundingText(beforeLength: Int, afterLength: Int): Boolean {
            // Deleting more than our copy holds: the rest is text that was on the computer before.
            val cur = Selection.getSelectionStart(ed).coerceAtLeast(0)
            val extraBefore = (beforeLength - cur).coerceIn(0, 64)
            val extraAfter = (afterLength - (ed.length - Selection.getSelectionEnd(ed).coerceAtLeast(0))).coerceIn(0, 64)
            val r = super.deleteSurroundingText(beforeLength, afterLength)
            changed()
            repeat(extraBefore) { onKey("back", emptyList()) }
            repeat(extraAfter) { onKey("del", emptyList()) }
            return r
        }

        @RequiresApi(24)
        override fun deleteSurroundingTextInCodePoints(beforeLength: Int, afterLength: Int): Boolean {
            val cur = Selection.getSelectionStart(ed).coerceAtLeast(0)
            val end = Selection.getSelectionEnd(ed).coerceAtLeast(0)
            val s = ed.toString()
            val extraBefore = (beforeLength - s.codePointCount(0, cur)).coerceIn(0, 64)
            val extraAfter = (afterLength - s.codePointCount(end, s.length)).coerceIn(0, 64)
            val r = super.deleteSurroundingTextInCodePoints(beforeLength, afterLength)
            changed()
            repeat(extraBefore) { onKey("back", emptyList()) }
            repeat(extraAfter) { onKey("del", emptyList()) }
            return r
        }

        /** Moving the cursor (sliding on the space bar) moves the computer's cursor; our copy stays at its end. */
        override fun setSelection(start: Int, end: Int): Boolean {
            val cur = Selection.getSelectionStart(ed)
            if (start == end && start in 0..ed.length && cur >= 0) {
                val d = (start - cur).coerceIn(-100, 100)
                repeat(abs(d)) { onKey(if (d < 0) "left" else "right", emptyList()) }
            }
            report()
            return true
        }

        override fun performEditorAction(actionCode: Int): Boolean {
            onKey("enter", emptyList())
            return true
        }

        @Suppress("DEPRECATION") // ACTION_MULTIPLE with characters: how old keyboards send text as a key event
        override fun sendKeyEvent(event: KeyEvent): Boolean {
            if (event.action == KeyEvent.ACTION_MULTIPLE && event.keyCode == KeyEvent.KEYCODE_UNKNOWN) {
                event.characters?.let { emit(it) }
                return true
            }
            if (event.action != KeyEvent.ACTION_DOWN) return true
            val mods = buildList {
                if (event.isCtrlPressed) add("ctrl")
                if (event.isAltPressed) add("alt")
            }
            val name = when (event.keyCode) {
                KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_NUMPAD_ENTER -> "enter"
                KeyEvent.KEYCODE_DEL -> "back"
                KeyEvent.KEYCODE_FORWARD_DEL -> "del"
                KeyEvent.KEYCODE_TAB -> "tab"
                KeyEvent.KEYCODE_ESCAPE -> "esc"
                KeyEvent.KEYCODE_DPAD_LEFT -> "left"
                KeyEvent.KEYCODE_DPAD_RIGHT -> "right"
                KeyEvent.KEYCODE_DPAD_UP -> "up"
                KeyEvent.KEYCODE_DPAD_DOWN -> "down"
                KeyEvent.KEYCODE_MOVE_HOME -> "home"
                KeyEvent.KEYCODE_MOVE_END -> "end"
                KeyEvent.KEYCODE_PAGE_UP -> "pgup"
                KeyEvent.KEYCODE_PAGE_DOWN -> "pgdn"
                else -> null
            }
            if (name != null) { onKey(name, mods); return true }
            val ch = event.getUnicodeChar(event.metaState and (KeyEvent.META_CTRL_MASK or KeyEvent.META_ALT_MASK).inv())
            if (ch > 0 && (ch and KeyCharacterMap.COMBINING_ACCENT) == 0) {
                val s = String(Character.toChars(ch))
                if (mods.isEmpty()) emit(s) else onKey(s.lowercase(), mods)
            }
            return true
        }
    }
}
