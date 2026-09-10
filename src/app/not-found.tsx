import { Container } from '@/components/layout/container'

export default function NotFound() {
  return (
    <Container className="py-24">
      <h1 className="text-navy text-4xl font-semibold sm:text-5xl">That tide went out.</h1>
      <p className="text-navy/85 mt-4 text-lg">There is no page at this address.</p>
      <a
        href="/"
        className="bg-berry font-display hover:bg-berry-deep mt-8 inline-flex rounded-full px-6 py-3 text-lg font-semibold text-white"
      >
        Back to the shore
      </a>
    </Container>
  )
}
