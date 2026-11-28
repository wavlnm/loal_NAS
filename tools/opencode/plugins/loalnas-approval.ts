import { Plugin } from "@opencode/plugin"

const BING_RSS_URL = "https://cn.bing.com/search"
const BING_USER_AGENT =
  "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
  "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36"
const BING_TIMEOUT_MS = 8_000
const MAX_RESULTS = 8

type SearchResult = {
  url: string
  title: string
  content: string
  time: Record<string, never>
}

function decodeXmlEntities(value: string): string {
  return value.replace(
    /&(#x?[0-9a-fA-F]+|[a-zA-Z]+);/g,
    (match, code: string) => {
      if (code.startsWith("#")) {
        const hex = code[1]?.toLowerCase() === "x"
        const point = Number.parseInt(code.slice(hex ? 2 : 1), hex ? 16 : 10)
        if (Number.isFinite(point)) {
          try {
            return String.fromCodePoint(point)
          } catch {
            return match
          }
        }
        return match
      }
      const named: Record<string, string> = {
        amp: "&",
        lt: "<",
        gt: ">",
        quot: '"',
        apos: "'",
        nbsp: " ",
      }
      return named[code.toLowerCase()] ?? match
    },
  )
}

function cleanXmlText(value: string): string {
  const trimmed = value.trim()
  const cdata = /^<!\[CDATA\[([\s\S]*?)\]\]>$/.exec(trimmed)
  return decodeXmlEntities(cdata ? cdata[1] : trimmed)
    .replace(/<[^>]*>/g, " ")
    .replace(/\s+/g, " ")
    .trim()
}

function xmlField(block: string, tag: string): string {
  const match = new RegExp(`<${tag}>([\\s\\S]*?)</${tag}>`, "i").exec(block)
  return match ? match[1] : ""
}

function truncate(value: string, limit: number): string {
  return value.length <= limit ? value : `${value.slice(0, limit)}...`
}

function parseBingRss(xml: string): SearchResult[] {
  const results: SearchResult[] = []
  const seen = new Set<string>()
  for (const match of xml.matchAll(/<item>([\s\S]*?)<\/item>/gi)) {
    const block = match[1]
    const title = truncate(cleanXmlText(xmlField(block, "title")), 200)
    const link = decodeXmlEntities(xmlField(block, "link")).trim()
    const content = truncate(cleanXmlText(xmlField(block, "description")), 300)
    if (!title || !link) {
      continue
    }

    let url: URL
    try {
      url = new URL(link)
    } catch {
      continue
    }
    if (url.protocol !== "http:" && url.protocol !== "https:") {
      continue
    }
    const key = url.toString()
    if (seen.has(key)) {
      continue
    }
    seen.add(key)
    results.push({ url: key, title, content, time: {} })
    if (results.length >= MAX_RESULTS) {
      break
    }
  }
  return results
}

async function searchBing(
  query: string,
  signal?: AbortSignal,
): Promise<SearchResult[]> {
  const text = query.trim()
  if (!text) {
    return []
  }

  const controller = new AbortController()
  const timer = setTimeout(() => controller.abort(), BING_TIMEOUT_MS)
  const onAbort = () => controller.abort()
  if (signal) {
    if (signal.aborted) {
      controller.abort()
    } else {
      signal.addEventListener("abort", onAbort, { once: true })
    }
  }

  try {
    const params = new URLSearchParams({
      q: text,
      format: "rss",
      count: "10",
    })
    const response = await fetch(`${BING_RSS_URL}?${params.toString()}`, {
      headers: {
        "user-agent": BING_USER_AGENT,
        accept: "application/rss+xml, application/xml;q=0.9, */*;q=0.8",
        "accept-language": "zh-CN,zh;q=0.9,en;q=0.8",
      },
      signal: controller.signal,
    })
    if (!response.ok) {
      throw new Error(`Bing 搜索失败：HTTP ${response.status}`)
    }

    const xml = await response.text()
    if (!xml.includes("<rss")) {
      throw new Error("Bing 返回了非 RSS 内容，可能触发了访问限制")
    }
    return parseBingRss(xml)
  } catch (error) {
    if (controller.signal.aborted && !signal?.aborted) {
      throw new Error("Bing 搜索超时，请稍后重试")
    }
    throw error
  } finally {
    clearTimeout(timer)
    signal?.removeEventListener("abort", onAbort)
  }
}

type ApprovalDecision = "allow" | "deny"

type ApprovalResponse = {
  decision?: ApprovalDecision
  reason?: string
}

/**
 * 千私云审批桥：每条 shell 命令执行前都询问本地 C# 服务。
 * 非敏感命令由服务端立即放行；敏感命令会挂起，等待手机点按钮或超时拒绝。
 */
export default Plugin.define({
  id: "loalnas-approval",
  async setup(ctx) {
    await ctx.shell.hook("create.before", async (event) => {
      // 不要把模型 Key / 审批 token 暴露给被执行的命令。
      if (event.env) {
        delete event.env.NAS_AGENT_API_KEY
        delete event.env.LOALNAS_AGENT_APPROVAL_TOKEN
      }

      const url = process.env.LOALNAS_AGENT_APPROVAL_URL
      const token = process.env.LOALNAS_AGENT_APPROVAL_TOKEN
      if (!url || !token) {
        event.command = "printf 'LOALNAS_DENIED\\n' >&2; exit 1"
        throw new Error("LOALNAS_DENIED: approval bridge is not configured")
      }

      let response: ApprovalResponse = { decision: "deny" }
      try {
        const result = await fetch(url, {
          method: "POST",
          headers: {
            "content-type": "application/json",
            authorization: `Bearer ${token}`,
          },
          body: JSON.stringify({
            kind: "bash",
            command: event.command,
            cwd: event.cwd,
          }),
          signal: AbortSignal.timeout(130_000),
        })
        response = (await result.json()) as ApprovalResponse
      } catch {
        response = { decision: "deny", reason: "approval bridge unreachable" }
      }

      if (response.decision !== "allow") {
        // 兜底：即使 hook 异常被吞掉，也不执行原命令。
        event.command = "printf 'LOALNAS_DENIED\\n' >&2; exit 1"
        event.timeout = 1_000
        throw new Error(`LOALNAS_DENIED: ${response.reason ?? "denied"}`)
      }
    })

    try {
      await ctx.websearch.transform((editor: any) => {
        try {
          editor.add({
            id: "bing-rss",
            name: "Bing RSS",
            execute: async (
              input: { query?: unknown },
              context?: { signal?: AbortSignal },
            ) => searchBing(String(input?.query ?? ""), context?.signal),
          })
          if (editor.default.get() === undefined) {
            editor.default.set("bing-rss")
          }
        } catch (error) {
          console.error("loalnas websearch provider setup failed", error)
        }
      })
    } catch (error) {
      console.error("loalnas websearch registration failed", error)
    }
  },
})
