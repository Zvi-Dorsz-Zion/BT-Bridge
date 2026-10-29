package com.btbridge.app

import com.btbridge.app.core.RemoteItem

/**
 * File order like the file managers people know (same rules as the Windows side): folders first; names in
 * "natural" order (2 before 10); folders that collect new files (Downloads, camera, screenshots) newest first.
 */
object FileOrder {
    private val recentFirst = setOf(
        "downloads", "download", "הורדות", "dcim", "camera", "מצלמה", "screenshots", "צילומי מסך",
        "whatsapp images", "whatsapp video", "whatsapp documents", "telegram", "recordings", "bt bridge", "גשר בלוטוס",
    )

    fun default(path: String): Pair<String, Boolean> {
        val name = path.trimEnd('\\', '/').split('\\', '/').lastOrNull()?.lowercase() ?: ""
        return if (name in recentFirst) "date" to true else "name" to false
    }

    /** Natural order: digit runs compare as numbers, the rest case-insensitively. */
    val natural = Comparator<String> { a, b ->
        var i = 0
        var j = 0
        while (i < a.length && j < b.length) {
            val ca = a[i]
            val cb = b[j]
            if (ca.isDigit() && cb.isDigit()) {
                val si = i
                val sj = j
                while (i < a.length && a[i].isDigit()) i++
                while (j < b.length && b[j].isDigit()) j++
                val na = a.substring(si, i).trimStart('0')
                val nb = b.substring(sj, j).trimStart('0')
                if (na.length != nb.length) return@Comparator na.length - nb.length
                val c = na.compareTo(nb)
                if (c != 0) return@Comparator c
            } else {
                val c = ca.lowercaseChar().compareTo(cb.lowercaseChar())
                if (c != 0) return@Comparator c
                i++
                j++
            }
        }
        (a.length - i) - (b.length - j)
    }

    fun sort(items: List<RemoteItem>, by: String, desc: Boolean): List<RemoteItem> {
        val byName = compareBy(natural) { it: RemoteItem -> it.name }
        val cmp: Comparator<RemoteItem> = when (by) {
            "date" -> compareBy<RemoteItem> { it.mtime }.thenComparing(byName)
            "size" -> compareBy<RemoteItem> { it.size }.thenComparing(byName)
            "type" -> compareBy<RemoteItem> { it.name.substringAfterLast('.', "").lowercase() }.thenComparing(byName)
            else -> byName
        }
        val ordered = if (desc) cmp.reversed() else cmp
        val folders = items.filter { it.isDir }.sortedWith(if (by == "size") byName else ordered)
        return folders + items.filter { !it.isDir }.sortedWith(ordered)
    }

    fun label(by: String, desc: Boolean) = when (by) {
        "date" -> if (desc) "לפי תאריך, החדשים קודם" else "לפי תאריך, הישנים קודם"
        "size" -> if (desc) "לפי גודל, הגדולים קודם" else "לפי גודל, הקטנים קודם"
        "type" -> "לפי סוג"
        else -> if (desc) "לפי שם (ת-א)" else "לפי שם (א-ת)"
    }
}
