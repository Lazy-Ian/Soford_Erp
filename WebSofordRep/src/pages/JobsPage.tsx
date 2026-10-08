import { useCallback, useEffect, useState } from 'react'
import { ChevronDown, ChevronRight, RefreshCw } from 'lucide-react'
import { api, errorText, type ImportJob, type PublishJob } from '../api'
import { Pill, ResultList, Spinner } from '../components/ui'
import { useApp } from '../context'
import { useInitialLoad } from '../hooks'
import { downloadUrl, formatDate, jobKindLabel, jobStatusLabel, jobStatusTone } from '../format'

const importStatusLabel: Record<ImportJob['status'], string> = {
  Previewed: '等待确认', Queued: '排队中', Running: '执行中', Completed: '已完成',
  CompletedWithWarnings: '完成但有警告', Failed: '失败', Interrupted: '已中断',
}

const importModeLabel: Record<ImportJob['mode'], string> = {
  upsert: '新增并更新', 'create-only': '仅新增', 'update-only': '仅更新', 'stock-price': '只更新库存价格',
}

export function JobsPage() {
  const { notify } = useApp()
  const [jobs, setJobs] = useState<PublishJob[] | null>(null)
  const [imports, setImports] = useState<ImportJob[] | null>(null)
  const [open, setOpen] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      const [operations, importJobs] = await Promise.all([
        api<PublishJob[]>('/api/catalog/publish-jobs?take=100'),
        api<ImportJob[]>('/api/catalog/import-jobs?take=50'),
      ])
      setJobs(operations)
      setImports(importJobs)
    } catch (err) {
      notify('error', errorText(err, '加载后台任务失败'))
    }
  }, [notify])

  const commitImport = async (id: string) => {
    try {
      await api(`/api/catalog/import-jobs/${id}/commit`, { method: 'POST' })
      await load()
    } catch (err) {
      notify('error', errorText(err, '提交导入任务失败'))
    }
  }

  useInitialLoad(load)

  // Keep refreshing while any job is still running.
  useEffect(() => {
    if (!jobs?.some((job) => job.status === 'Queued' || job.status === 'Running')
      && !imports?.some((job) => job.status === 'Queued' || job.status === 'Running')) return
    const timer = window.setTimeout(() => void load(), 2000)
    return () => window.clearTimeout(timer)
  }, [jobs, imports, load])

  return (
    <div className="page">
      <section className="card">
        <div className="card-head">
          <div>
            <strong>后台任务</strong>
            <span>发布、同步状态/库存/价格、上下架、预测类目都在后台执行，这里记录每个商品的结果。</span>
          </div>
          <button type="button" onClick={() => void load()}><RefreshCw size={16} /> 刷新</button>
        </div>
        {jobs === null && <div className="empty small"><Spinner size={20} /></div>}
        {jobs?.length === 0 && <div className="empty small"><span>还没有后台任务。</span></div>}
        <div className="job-list">
          {jobs?.map((job) => (
            <article key={job.id} className="job">
              <button type="button" className="job-head" onClick={() => setOpen(open === job.id ? null : job.id)}>
                {open === job.id ? <ChevronDown size={16} /> : <ChevronRight size={16} />}
                <strong>{jobKindLabel[job.kind ?? 'publish']}</strong>
                <Pill tone={jobStatusTone[job.status]}>{jobStatusLabel[job.status]}</Pill>
                <span>{formatDate(job.createdAt)}{job.createdByName ? ` · ${job.createdByName}` : ''}</span>
                <span className="muted">共 {job.total} 个 · 成功 {job.succeeded} · 失败 {job.failed}</span>
                <div className="progress small"><div style={{ width: `${job.total ? (job.processed / job.total) * 100 : 0}%` }} /></div>
              </button>
              {open === job.id && (
                <div className="job-body">
                  {job.message && <p className="muted">{job.message}</p>}
                  <ResultList items={job.items} />
                </div>
              )}
            </article>
          ))}
        </div>
      </section>
      <section className="card">
        <div className="card-head">
          <div>
            <strong>表格导入</strong>
            <span>预检、后台写入、自动质检和错误报告。等待确认的任务尚未修改商品。</span>
          </div>
        </div>
        {imports === null && <div className="empty small"><Spinner size={20} /></div>}
        {imports?.length === 0 && <div className="empty small"><span>还没有表格导入任务。</span></div>}
        <div className="job-list">
          {imports?.map((job) => (
            <article key={job.id} className="job">
              <button type="button" className="job-head" onClick={() => setOpen(open === job.id ? null : job.id)}>
                {open === job.id ? <ChevronDown size={16} /> : <ChevronRight size={16} />}
                <strong>表格导入</strong>
                <Pill tone={job.status === 'Failed' || job.status === 'Interrupted' ? 'bad' : job.status === 'CompletedWithWarnings' ? 'warn' : 'good'}>{importStatusLabel[job.status]}</Pill>
                <span>{formatDate(job.createdAt)} · {job.createdByName}</span>
                <span className="muted">新增 {job.created} · 更新 {job.updated} · 跳过 {job.skipped}</span>
                <div className="progress small"><div style={{ width: `${job.total ? (job.processed / job.total) * 100 : 0}%` }} /></div>
              </button>
              {open === job.id && (
                <div className="job-body">
                  <p><b>{job.fileName}</b></p>
                  <p className="muted">模式：{importModeLabel[job.mode]} · 共 {job.total} 行</p>
                  {job.message && <p className="muted">{job.message}</p>}
                  {['Previewed', 'Interrupted', 'Failed'].includes(job.status) && (
                    <button type="button" className="primary" onClick={() => void commitImport(job.id)}>{job.status === 'Previewed' ? '确认并后台导入' : '重新执行'}</button>
                  )}
                  {job.warningCount > 0 && <button type="button" onClick={() => downloadUrl(`/api/catalog/import-jobs/${job.id}/errors.xlsx`)}>下载错误明细</button>}
                  {job.warnings.length > 0 && <ul className="issues">{job.warnings.slice(0, 50).map((warning, index) => <li key={index} className="warning">{warning}</li>)}</ul>}
                </div>
              )}
            </article>
          ))}
        </div>
      </section>
    </div>
  )
}
