package leontec.jp.synclogs

import android.content.Context
import android.content.res.Configuration
import android.util.Log
import java.util.Locale

/**
 * Applies the user's chosen UI language by wrapping a base [Context] with a forced locale.
 *
 * The picker stores one of: "system" (follow the device locale — Android then resolves
 * values-vi / values-ja / values(=English) automatically), or an explicit "en" / "vi" / "ja".
 * For an explicit code we build a configuration-overridden context so every resource lookup
 * (including Compose `stringResource`) resolves in that language regardless of the device locale.
 *
 * Used from [MainActivity.attachBaseContext]; switching language calls `recreate()` so the new
 * base context is built.
 */
object LocaleHelper {
    private const val TAG = "LOCALE_HELPER"

    /** Supported explicit language codes (besides "system"). */
    val SUPPORTED = listOf("en", "vi", "ja")

    fun wrap(context: Context, lang: String): Context {
        if (lang.isBlank() || lang == "system" || lang !in SUPPORTED) {
            Log.d(TAG, "Locale = system/default (lang='$lang').")
            return context
        }
        val locale = Locale(lang)
        Locale.setDefault(locale)
        val config = Configuration(context.resources.configuration)
        config.setLocale(locale)
        Log.i(TAG, "Áp dụng ngôn ngữ UI: '$lang'.")
        return context.createConfigurationContext(config)
    }
}
