/** Where somebody is in setting up: "Step 1 of 2", and the same as a bar. */
export function WizardSteps({ step, total }: { step: number; total: number }) {
  return (
    <div className="wizard-steps">
      <span>
        Step {step} of {total}
      </span>
      <div
        className="wizard-bar"
        role="progressbar"
        aria-label="Setup progress"
        aria-valuemin={1}
        aria-valuemax={total}
        aria-valuenow={step}
      >
        {Array.from({ length: total }, (_, index) => (
          <span key={index} className={index < step ? "done" : ""} />
        ))}
      </div>
    </div>
  );
}
