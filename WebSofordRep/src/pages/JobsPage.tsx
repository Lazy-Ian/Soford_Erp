import { useCallback, useEffect, useState } from 'react'
import { ChevronDown, ChevronRight, RefreshCw } from 'lucide-react'
import { api, errorText, type PublishJob } from '../api'
import { Pill, ResultList, Spinner } from '../components/ui'
import { useApp } from '../context'
import { useInitialLoad } from '../hooks'
import { formatDate, jobStatusLabel, jobStatusTone } from '../format'

export function JobsPage() {
  const { notify } = useApp()
  const [jobs, setJobs] = useState<PublishJob[] | null>(null)
  const [open, setOpen] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      setJobs(await api<PublishJob[]>('/api/catalog/publish-jobs?take=100'))
    } catch (err) {
      notify('error', errorText(err, '加载发布任务失败'))
    }
  }, [notify])

  useInitialLoad(load)

  // Keep refreshing while any job is still running.
  useEffect(() => {
    if (!jobs?.some((job) => job.status === 'Queued' || job.status === 'Running')) return
    const timer = window.setTimeout(() => void load(), 2000)
    return () => window.clearTimeout(timer)
  }, [jobs, load])

  return (
    <div className="page">
      <section className="card">
        <div className="card-head">
          <div>
            <strong>发布任务</strong>
            <span>每次「发布到 Alibaba」都会生成一个后台任务，记录每个商品的结果。</span>
          </div>
          <button type="button" onClick={() => void load()}><RefreshCw size={16} /> 刷新</button>
        </div>
        {jobs === null && <div className="empty small"><Spinner size={20} /></div>}
        {jobs?.length === 0 && <div className="empty small"><span>还没有发布任务。</span></div>}
        <div className="job-list">
          {jobs?.map((job) => (
            <article key={job.id} className="job">
              <button type="button" className="job-head" onClick={() => setOpen(open === job.id ? null : job.id)}>
                {open === job.id ? <ChevronDown size={16} /> : <ChevronRight size={16} />}
                <Pill tone={jobStatusTone[job.status]}>{jobStatusLabel[job.status]}</Pill>
                <span>{formatDate(job.createdAt)}</span>
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
    </div>
  )
}
