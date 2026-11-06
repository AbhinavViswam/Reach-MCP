package app.reach.android.screen

import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.RectF
import com.google.mlkit.vision.common.InputImage
import com.google.mlkit.vision.text.TextRecognition
import com.google.mlkit.vision.text.latin.TextRecognizerOptions
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlin.coroutines.resume

/**
 * On-phone text reading with ML Kit's bundled Latin recogniser: offline, nothing leaves the phone. Reads Latin
 * script (English and other Latin-alphabet languages); other scripts come back empty or as stray marks, which
 * Vision.extra drops.
 */
object Ocr {
    private val client by lazy { TextRecognition.getClient(TextRecognizerOptions.DEFAULT_OPTIONS) }

    /** Lines of text on [full] (screen-sized), in screen pixels. Large screens are read at 1600 px for speed. */
    suspend fun lines(full: Bitmap): List<OcrLine> {
        val scale = minOf(1f, 1600f / maxOf(full.width, full.height))
        val img = if (scale < 1f) Bitmap.createScaledBitmap(full, (full.width * scale).toInt(), (full.height * scale).toInt(), true) else full
        val back = 1f / scale
        return try {
            suspendCancellableCoroutine { cont ->
                client.process(InputImage.fromBitmap(img, 0))
                    .addOnSuccessListener { text ->
                        val out = text.textBlocks.flatMap { b -> b.lines }.mapNotNull { l ->
                            val r = l.boundingBox ?: return@mapNotNull null
                            OcrLine(l.text, Box((r.left * back).toInt(), (r.top * back).toInt(), (r.right * back).toInt(), (r.bottom * back).toInt()))
                        }
                        if (cont.isActive) cont.resume(out)
                    }
                    .addOnFailureListener { if (cont.isActive) cont.resume(emptyList()) }
            }
        } finally {
            if (img !== full) img.recycle()
        }
    }

    /**
     * Draws a numbered box on each of [elements] onto a copy of [full] ("marks" view): the model can point at
     * "box 7" and act on its ID. Colours alternate so neighbouring boxes stay apart on light and dark screens.
     */
    fun marks(full: Bitmap, elements: List<Element>): Bitmap {
        val out = full.copy(Bitmap.Config.ARGB_8888, true)
        val c = Canvas(out)
        val stroke = maxOf(2f, full.width / 360f)
        val textSize = maxOf(18f, full.width / 36f)
        val colors = intArrayOf(Color.rgb(255, 64, 129), Color.rgb(0, 176, 255), Color.rgb(255, 171, 0), Color.rgb(0, 200, 83))
        val box = Paint().apply { style = Paint.Style.STROKE; strokeWidth = stroke; isAntiAlias = true }
        val tag = Paint().apply { style = Paint.Style.FILL; isAntiAlias = true }
        val label = Paint().apply { color = Color.WHITE; this.textSize = textSize; isFakeBoldText = true; isAntiAlias = true }
        elements.forEachIndexed { i, e ->
            val col = colors[i % colors.size]
            box.color = col
            tag.color = col
            val b = e.bounds
            c.drawRect(b.left.toFloat(), b.top.toFloat(), b.right.toFloat(), b.bottom.toFloat(), box)
            val n = (i + 1).toString()
            val w = label.measureText(n) + textSize * 0.5f
            val h = textSize * 1.2f
            val x = b.left.toFloat()
            val y = (b.top - h).coerceAtLeast(0f)
            c.drawRoundRect(RectF(x, y, x + w, y + h), h / 4, h / 4, tag)
            c.drawText(n, x + textSize * 0.25f, y + h - textSize * 0.3f, label)
        }
        return out
    }
}
