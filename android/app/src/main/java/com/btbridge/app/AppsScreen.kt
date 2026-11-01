package com.btbridge.app

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Language
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Window
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.btbridge.app.core.Session
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject

/**
 * "Programs on the computer": the separate browser, and the computer's open windows. Picking one shows and controls
 * only that program; the computer's screen and mouse stay with whoever sits at it.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AppsScreen(s: Session, onBack: () -> Unit, onOpen: (RemoteTarget) -> Unit) {
    var windows by remember { mutableStateOf<List<JSONObject>>(emptyList()) }
    var browser by remember { mutableStateOf(false) }
    var loading by remember { mutableStateOf(true) }
    var error by remember { mutableStateOf<String?>(null) }
    var reload by remember { mutableIntStateOf(0) }
    BackHandler(onBack = onBack)

    LaunchedEffect(reload) {
        loading = true
        try {
            val res = withContext(Dispatchers.IO) { s.request(JSONObject().put("op", "windows")) }
            val arr = res.optJSONArray("items")
            windows = if (arr == null) emptyList() else (0 until arr.length()).map { arr.getJSONObject(it) }
            browser = res.optBoolean("browser")
            error = null
        } catch (e: Exception) {
            error = com.btbridge.app.core.Heb.of(e)
        } finally {
            loading = false
        }
    }

    Scaffold(topBar = {
        TopAppBar(
            title = { Text("תוכנות במחשב") },
            navigationIcon = { IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Filled.ArrowBack, "חזור") } },
            actions = { IconButton(onClick = { reload++ }) { Icon(Icons.Filled.Refresh, "רענן") } },
        )
    }) { pad ->
        Column(Modifier.padding(pad).fillMaxSize()) {
            if (loading) LinearProgressIndicator(Modifier.fillMaxWidth())
            Text("שולטים רק בתוכנה שבוחרים, בלי לגעת במסך ובעכבר של המחשב.",
                Modifier.padding(16.dp, 8.dp), style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant)
            error?.let { Text(it, Modifier.padding(16.dp), color = MaterialTheme.colorScheme.error) }
            LazyColumn(Modifier.weight(1f)) {
                if (browser && s.has("browser")) item {
                    ListItem(
                        headlineContent = { Text("דפדפן נפרד") },
                        supportingContent = { Text("גלישה דרך המחשב, בדפדפן שרק הטלפון רואה. הדפדפן במחשב לא מושפע.") },
                        leadingContent = { Icon(Icons.Filled.Language, null, tint = MaterialTheme.colorScheme.primary) },
                        modifier = Modifier.clickable { onOpen(RemoteTarget("דפדפן נפרד", browser = true)) },
                    )
                    HorizontalDivider()
                }
                if (s.has("window")) items(windows, key = { it.optLong("h") }) { w ->
                    val title = w.optString("title")
                    ListItem(
                        headlineContent = { Text(title, maxLines = 2) },
                        supportingContent = {
                            Text(w.optString("app") + if (w.optBoolean("min")) " · ממוזער (ייפתח מאחורי החלונות האחרים)" else "")
                        },
                        leadingContent = { Icon(Icons.Filled.Window, null) },
                        modifier = Modifier.clickable { onOpen(RemoteTarget(title, window = w.optLong("h"))) },
                    )
                    HorizontalDivider()
                }
                if (!loading && windows.isEmpty() && error == null) item { Text("אין חלונות פתוחים במחשב.", Modifier.padding(16.dp)) }
            }
        }
    }
}
