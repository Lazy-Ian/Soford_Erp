import { useEffect } from 'react'

/** Runs a loader once after mount (and again if it changes), outside the effect body so state updates don't cascade. */
export function useInitialLoad(load: () => Promise<void>) {
  useEffect(() => {
    const timer = window.setTimeout(() => void load(), 0)
    return () => window.clearTimeout(timer)
  }, [load])
}
