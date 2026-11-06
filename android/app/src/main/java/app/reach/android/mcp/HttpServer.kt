package app.reach.android.mcp

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import java.io.BufferedInputStream
import java.io.ByteArrayOutputStream
import java.io.InputStream
import java.io.OutputStream
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketException
import java.net.SocketTimeoutException
import java.security.MessageDigest
import java.util.UUID

/**
 * MCP Streamable HTTP, the small part of it Reach needs (spec/CONTRACT.md §1): POST /mcp with one JSON-RPC
 * message, reply as application/json. GET /health without a token. Keep-alive connections. Plain java.net, so it
 * runs and is tested on the PC (HttpServerTest).
 */
class HttpServer(
    private val handler: McpHandler,
    private val token: () -> String,
    private val port: Int,
    /** false: 127.0.0.1 only (USB through adb forward). true: every network the phone is on. */
    private val allNetworks: Boolean,
    /** POST /approve from Reach for Windows: (what, detail, from) → did the user allow it. Null: not offered. */
    private val approve: (suspend (String, String, String) -> Boolean)? = null,
    /** Who may connect (Tailscale mode: this phone and Tailscale devices). Null: everyone the socket accepts. */
    private val allow: ((InetAddress) -> Boolean)? = null,
) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var socket: ServerSocket? = null
    private var acceptJob: Job? = null
    @Volatile var boundPort: Int = -1
        private set

    fun start() {
        val s = ServerSocket()
        s.reuseAddress = true
        val addr = if (allNetworks) InetAddress.getByName("0.0.0.0") else InetAddress.getLoopbackAddress()
        s.bind(InetSocketAddress(addr, port))
        socket = s
        boundPort = s.localPort
        acceptJob = scope.launch {
            while (isActive) {
                val c = try { s.accept() } catch (e: SocketException) { break }
                if (allow != null && !allow.invoke(c.inetAddress)) { runCatching { c.close() }; continue }
                launch { serve(c) }
            }
        }
    }

    fun stop() {
        runCatching { socket?.close() }
        scope.cancel()
    }

    private suspend fun serve(c: Socket) {
        c.use { sock ->
            sock.soTimeout = KEEP_ALIVE_MS
            val input = BufferedInputStream(sock.getInputStream())
            val out = sock.getOutputStream()
            while (true) {
                val req = try { readRequest(input) } catch (e: SocketTimeoutException) { return } catch (e: Exception) { return } ?: return
                val keepAlive = !req.header("connection").equals("close", ignoreCase = true)
                val resp = route(req)
                try {
                    write(out, resp, keepAlive)
                } catch (e: Exception) {
                    return
                }
                if (!keepAlive) return
            }
        }
    }

    private suspend fun route(req: Request): Response {
        val path = req.path.substringBefore('?')
        if (path == "/health" && req.method == "GET") return Response(200, "text/plain", "reach ok")
        if (path != "/mcp" && path != "/approve") return Response(404, "text/plain", "not found")

        // A web page in a browser on this device must not be able to call the server (DNS rebinding).
        req.header("origin")?.let { origin ->
            val host = origin.substringAfter("://").substringBefore('/').substringBeforeLast(':')
            if (host != "localhost" && host != "127.0.0.1" && host != "[::1]") return Response(403, "text/plain", "origin not allowed")
        }
        if (!authorized(req.header("authorization"))) {
            return Response(401, "application/json", """{"error":"missing or wrong Reach token"}""",
                extra = mapOf("WWW-Authenticate" to "Bearer"))
        }
        if (path == "/approve") return approveRoute(req)
        return when (req.method) {
            "POST" -> {
                val reply = handler.handle(req.body.toString(Charsets.UTF_8))
                val session = req.header("mcp-session-id") ?: UUID.randomUUID().toString()
                if (reply == null) Response(202, "text/plain", "", extra = mapOf("Mcp-Session-Id" to session))
                else Response(200, "application/json", reply, extra = mapOf("Mcp-Session-Id" to session))
            }
            "DELETE" -> Response(200, "text/plain", "")
            else -> Response(405, "text/plain", "use POST", extra = mapOf("Allow" to "POST"))
        }
    }

    /** The PC asks the user on this phone (spec/POLICY.md "phone approves PC"). Same token as /mcp. */
    private suspend fun approveRoute(req: Request): Response {
        val ask = approve ?: return Response(404, "text/plain", "not found")
        if (req.method != "POST") return Response(405, "text/plain", "use POST", extra = mapOf("Allow" to "POST"))
        val o = runCatching { Json.parseToJsonElement(req.body.toString(Charsets.UTF_8)) as JsonObject }.getOrNull()
            ?: return Response(400, "application/json", "{\"error\":\"bad json\"}")
        fun s(k: String) = (o[k] as? JsonPrimitive)?.content
        val what = s("what")?.take(200) ?: return Response(400, "application/json", "{\"error\":\"what is needed\"}")
        val allowed = try { ask(what, s("detail").orEmpty().take(1000), s("from")?.take(60) ?: "another device") } catch (e: ToolError) { false }
        return Response(200, "application/json", buildJsonObject { put("allowed", allowed) }.toString())
    }

    private fun authorized(header: String?): Boolean {
        val given = header?.takeIf { it.startsWith("Bearer ", ignoreCase = true) }?.substring(7)?.trim() ?: return false
        val want = token()
        if (want.isEmpty()) return false
        return MessageDigest.isEqual(given.toByteArray(), want.toByteArray())
    }

    // -------------------------------------------------------------------------------------------- HTTP/1.1 parsing

    private class Request(val method: String, val path: String, val headers: Map<String, String>, val body: ByteArray) {
        fun header(name: String) = headers[name.lowercase()]
    }

    private class Response(val status: Int, val type: String, val body: String, val extra: Map<String, String> = emptyMap())

    private fun readRequest(input: InputStream): Request? {
        val first = readLine(input) ?: return null
        val parts = first.split(' ')
        if (parts.size < 2) return null
        val headers = HashMap<String, String>()
        while (true) {
            val line = readLine(input) ?: return null
            if (line.isEmpty()) break
            val i = line.indexOf(':')
            if (i > 0) headers[line.substring(0, i).trim().lowercase()] = line.substring(i + 1).trim()
            if (headers.size > 100) return null
        }
        val len = headers["content-length"]?.toIntOrNull() ?: 0
        if (len < 0 || len > MAX_BODY) return null
        val body = ByteArray(len)
        var read = 0
        while (read < len) {
            val n = input.read(body, read, len - read)
            if (n < 0) return null
            read += n
        }
        return Request(parts[0].uppercase(), parts[1], headers, body)
    }

    private fun readLine(input: InputStream): String? {
        val buf = ByteArrayOutputStream()
        while (true) {
            val b = input.read()
            if (b < 0) return if (buf.size() == 0) null else buf.toString(Charsets.UTF_8.name())
            if (b == '\n'.code) break
            if (b != '\r'.code) buf.write(b)
            if (buf.size() > 8192) return null
        }
        return buf.toString(Charsets.UTF_8.name())
    }

    private fun write(out: OutputStream, r: Response, keepAlive: Boolean) {
        val body = r.body.toByteArray(Charsets.UTF_8)
        val head = buildString {
            append("HTTP/1.1 ").append(r.status).append(' ').append(reason(r.status)).append("\r\n")
            append("Content-Type: ").append(r.type).append(if (r.type.startsWith("text/") || r.type.endsWith("json")) "; charset=utf-8" else "").append("\r\n")
            append("Content-Length: ").append(body.size).append("\r\n")
            append("Cache-Control: no-store\r\n")
            r.extra.forEach { (k, v) -> append(k).append(": ").append(v).append("\r\n") }
            append("Connection: ").append(if (keepAlive) "keep-alive" else "close").append("\r\n\r\n")
        }
        out.write(head.toByteArray(Charsets.US_ASCII))
        out.write(body)
        out.flush()
    }

    private fun reason(code: Int) = when (code) {
        200 -> "OK"; 202 -> "Accepted"; 400 -> "Bad Request"; 401 -> "Unauthorized"; 403 -> "Forbidden"
        404 -> "Not Found"; 405 -> "Method Not Allowed"; else -> "Error"
    }

    companion object {
        private const val KEEP_ALIVE_MS = 60_000
        private const val MAX_BODY = 2 * 1024 * 1024
    }
}
