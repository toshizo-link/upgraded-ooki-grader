# Phase 3: intake and grading accuracy implementation plan

**Goal:** Make the safe automatic range explicit, evaluate it reproducibly,
and verify ordered-scan routing with versioned local fixtures before a school
golden set is available.

1. Add failing validator and worker tests proving that incorrect, blank,
   partial, unreadable, ambiguous, and manual results wait for teacher review,
   while a clear locally verified correct result can continue automatically.
2. Apply the review boundary after local reconciliation so safe line-wrap and
   deterministic correct-answer repairs retain their existing behavior.
3. Add a reusable accuracy evaluator with overall and category metrics for
   outcome/point agreement, automatic-decision precision, incorrect-credit
   false positives, and teacher-review coverage.
4. Add a committed local evaluation matrix covering multiple accepted
   answers, test-wide Kanji policy behavior, partial credit, incorrect answers,
   and blanks. Record deterministic evidence without claiming school accuracy.
5. Process the committed synthetic blank and completed PDFs with the real
   preprocessing/alignment pipeline. Record hashes and routing thresholds, and
   keep weak or ambiguous matches in teacher review.
6. Update the grading specification, implementation status, and fixture guide;
   run focused and full test/build verification; generate immutable 0.9.13 new
   install and host-update media.

The school-approved Japanese answer set remains the final external acceptance
gate. It must be privacy-reviewed and teacher-adjudicated before unattended
assignment or grading can be enabled.
