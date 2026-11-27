import { Plugin } from "@opencode/plugin"

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
  },
})
