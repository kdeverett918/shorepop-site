import Image from 'next/image'
import { cn } from '@/lib/utils'

type PhoneFrameProps = {
  src: string
  alt: string
  className?: string
  priority?: boolean
  /** Rendered CSS width in px; the 360x640 capture scales to fit. */
  width?: number
  /** Upper bound as a share of the viewport so pairs of phones fit on narrow screens. */
  maxVw?: number
}

/**
 * A simple CSS phone around a real 360x640 in-game capture. The bezel is the game's
 * navy so the frame reads as part of the same object as the screen.
 */
export function PhoneFrame({
  src,
  alt,
  className,
  priority,
  width = 250,
  maxVw = 44,
}: PhoneFrameProps) {
  return (
    <figure
      className={cn(
        'bg-navy shadow-phone relative shrink-0 rounded-[2.4rem] p-2.5 ring-1 ring-black/25',
        className,
      )}
      style={{ width: `min(${width}px, ${maxVw}vw)` }}
    >
      <div className="bg-navy overflow-hidden rounded-[1.9rem]">
        <Image
          src={src}
          alt={alt}
          width={360}
          height={640}
          priority={priority}
          className="block h-auto w-full"
          sizes={`(max-width: 640px) ${maxVw}vw, ${width}px`}
        />
      </div>
    </figure>
  )
}
