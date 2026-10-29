package com.btbridge.app.core

/** Turns any error into a Hebrew message for the user; the original goes to the log. */
object Heb {
    fun isHebrew(s: String?) = s?.any { it in '\u0590'..'\u05FF' } == true

    fun of(e: Throwable): String {
        val ex = (e as? java.util.concurrent.ExecutionException)?.cause ?: e
        val m = ex.message
        if (isHebrew(m)) return m!!
        Log.w("error (shown in Hebrew): $ex")
        return when (ex) {
            is java.util.concurrent.CancellationException -> "בוטל"
            is java.util.concurrent.TimeoutException, is java.net.SocketTimeoutException -> "אין תשובה מהמכשיר השני"
            is SecurityException -> "אין הרשאה"
            is java.io.FileNotFoundException -> "הקובץ לא נמצא, או שאין אליו גישה"
            is java.io.EOFException -> "החיבור נסגר"
            is java.io.IOException -> when {
                m?.contains("ENOSPC") == true -> "אין מספיק מקום בטלפון"
                m?.contains("read failed") == true || m?.contains("socket", ignoreCase = true) == true || m?.contains("connect", ignoreCase = true) == true ->
                    "החיבור נכשל. ודא שגשר בלוטוס פתוח במכשיר השני ושהבלוטוס דלוק."
                else -> "בעיה בחיבור או בקובץ"
            }
            else -> "שגיאה לא צפויה (הפרטים נשמרו ביומן)"
        }
    }
}