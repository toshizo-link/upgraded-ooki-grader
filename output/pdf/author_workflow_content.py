"""Human-authored scenario content and annotation coordinates for the field guide."""
from pathlib import Path
import json, os

E=Path(os.environ.get('OOKI_WORKFLOW_EVIDENCE', r'C:\Users\SEACL\Documents\OokiGrader-Workflow-20260919'))/'screenshots'
shots={int(p.name[:2]):p.name for p in E.glob('*.png')}
S={}
def shot(i,title,notes,crop,marks):
    S[i]={'file':shots[i] if isinstance(i,int) else shots[33],'title':title,'notes':notes,'crop':crop,'marks':marks}

shot(1,'Begin the day at the dashboard',['Next actions prioritizes unresolved work; an empty queue does not prove AI is healthy.','Open a reception or inspect the most recent paper from these panels.'],[270,70,1485,830],[[305,205,915,590],[940,205,1465,590]])
shot(2,'Find the right student before editing',['Use search, enrollment status, class, course and grade together.','Read the student number in the row; do not rely only on a similar name.'],[285,90,1480,790],[[310,240,1455,425],[310,440,1455,700]])
shot(3,'Student details and enrollment state',['Basic information is the post-save check for identity, class and course.','Enrollment controls change availability without deleting the history.'],[285,85,1480,780],[[310,325,1120,720],[1140,325,1460,530]])
shot(4,'Use aliases to support name matching',['Existing aliases remain separate from the official student name and number.','Choose the alias type and enter an actual alternate spelling.'],[285,85,1480,720],[[310,325,1120,595],[1140,325,1460,595]])
shot(5,'One result is a point, not a trend',['Set the date range and subject before interpreting a chart.','The baseline contains one 66.7% result; there is no trend line yet.'],[285,85,1480,1150],[[310,325,1460,445],[310,460,1460,1065]])
shot(6,'Open an individual result from the student record',['The results tab lists finalized work for this student.','Follow the row to the result; confirm date and template version before exporting.'],[285,80,1480,600],[[310,215,920,300],[310,325,1460,465]])
shot(7,'Read a student change history',['Use history when two staff members disagree about what changed.','The history view was inspected; retention of every audit type across an upgrade remains unverified.'],[285,80,1480,690],[[310,215,1060,300],[310,325,1460,560]])
shot(8,'Select a reusable template',['Filter by state, subject, category, course, grade and test type.','Open the published version; archive is a separate action.'],[285,80,1480,780],[[310,240,1460,485],[310,505,925,675]])
shot(9,'Published template: compare criteria with the source',['Select a question and inspect the original source sheet.','Published criteria are read-only; the reception button creates a new session.'],[280,35,1485,1210],[[295,145,475,410],[1195,145,1480,815]])
shot(10,'Advanced rules are question-specific',['The advanced panel exposes grading policy. This screenshot is a read-only published template.','Confirm variants, point increments and review rules in a draft before publishing.'],[1130,100,1485,1270],[[1170,400,1475,865],[1170,875,1475,1240]])
shot(11,'A reception inherits its template metadata',['The selected template supplies title, subject, grade, category and course.','Only choose the actual test date and optional class; do not rename the template for every sitting.'],[385,360,1100,1070],[[415,480,1070,650],[415,650,1070,800]])
shot(12,'Locate the correct sitting of a test',['A session is a particular date/class using a published template.','State and date filters help separate an open reception from historical sessions.'],[285,85,1480,780],[[310,250,1460,570],[310,580,955,735]])
shot(13,'Prepare one-page files in student order',['Check pages per answer before selecting files.','The ordered uploader expects one-page PDF files grouped consecutively for each student.'],[285,80,1480,1170],[[310,395,1460,515],[310,520,1460,745]])
shot(14,'A finalized result has a stable score and identity',['Confirm student, date, total and finalized state.','Question rows retain answers and teacher corrections even when a question crop is unavailable.'],[285,65,1480,1010],[[310,245,1460,360],[310,390,1460,935]])
shot(15,'Finalized grading is read-only',['The full scan can remain available while per-question crops are absent.','The editor is disabled until an audited reopen is completed.'],[275,120,1470,1490],[[290,465,1010,1330],[1020,485,1460,1300]])
shot(16,'Name-review empty state',['This tab counts papers whose identity still needs a decision.','After successful assignment, a paper leaves this queue.'],[285,80,1480,620],[[310,195,900,280],[310,290,1460,560]])
shot(17,'Grading-review empty state',['This is a different queue from name review.','A zero count is only meaningful after background processing has settled.'],[285,80,1480,620],[[310,195,900,280],[310,290,1460,560]])
shot(18,'Finalization empty state',['No paper currently meets all release conditions.','This does not imply every uploaded paper is already finalized. Check session statuses.'],[285,80,1480,620],[[310,195,900,280],[310,290,1460,560]])
shot(19,'Choose report scope before exporting',['Filters select finalized results by student, template, subject, course, class and dates.','Checked rows and all filtered results are different export scopes.'],[285,85,1480,825],[[310,240,1460,490],[310,515,1460,735]])
shot(20,'Preview a bulk result export',['The host recomputes student and result counts before creation.','Acknowledge the scope only after checking both counts.'],[345,375,1160,870],[[375,490,1130,595],[375,605,1130,820]])
shot(21,'Wait for a verified bulk ZIP',['The job completed all 1/1 selected results.','Download the verified ZIP and reconcile the manifest with its PDFs.'],[315,395,1180,885],[[340,510,1150,620],[340,625,1150,825]])
shot(22,'CSV import begins with file and encoding',['Select a roster CSV and its encoding, then request a preview.','The browser file chooser was blocked in this environment; this import was executed through the same application API.'],[335,355,1190,970],[[365,495,1150,655],[365,665,1150,910]])
shot(23,'Verify imported rows in the roster',['The three synthetic CSV rows were added, increasing the roster from three to six.','Hana and Ken have Workflow A; Mai has Workflow B.'],[285,85,1480,1120],[[310,265,1460,455],[310,505,1460,1005]])
shot(24,'Filter the whole roster by class',['Workflow A returns Hana and Ken only.','Class-dependent workflows require the stored class, not merely text typed into an unsaved form.'],[285,85,1480,760],[[310,305,1460,405],[310,435,1460,625]])
shot(25,'Reproduced defect: class entered in student form',['WF-104 was created with grade, class and course filled.','The class is visibly Workflow A before saving; compare the next screenshot.'],[335,225,1185,1120],[[370,330,1150,655],[370,685,1150,815]])
shot(26,'Reproduced defect: class missing after save',['The new student exists and other fields persist.','Class displays a dash; API data also lacks the class. This is F01, not successful retention.'],[285,85,1480,790],[[310,315,1120,710],[320,530,780,640]])
shot(27,'Alias creation succeeded',['Riku Demo is stored as a romanized alias.','The generic row label does not change the API alias type, which was checked.'],[285,85,1480,680],[[310,325,1120,570],[1140,325,1460,570]])
shot(28,'Deactivate without deleting a student',['Confirm the displayed student identity.','Enrollment-ending changes the usable roster; it is not permanent record deletion.'],[475,470,995,830],[[500,515,965,685],[500,700,965,785]])
shot(29,'Inactive student is still viewable',['The status badge shows enrollment ended.','The detail record remains and offers reactivation.'],[285,85,1480,840],[[1260,100,1460,180],[1140,340,1460,535]])
shot(30,'Reactivation preserves the record',['The student is active again.','Existing identity fields and notes remain; the alias was retained.'],[285,85,1480,845],[[1260,100,1460,180],[310,335,1120,775]])
shot(31,'System health is not a single green light',['This installed host reports outstanding AI and backup items.','Maintenance, database/files and backup state must be checked independently.'],[280,90,1470,1310],[[295,290,1460,760],[295,795,1460,1300]])
shot(32,'Gemini 3.8 Flash is configured but not healthy',['Read the actual model ID, not an old friendly connection name.','The failure alert and inactive feature rows prevent claiming live AI acceptance.'],[280,90,1480,1240],[[300,320,880,785],[300,905,1460,1190]])
shot(33,'Price snapshots and budget guard',['Register verified prices for this exact model; do not invent a price or double count thinking tokens.','Warnings, hard caps, exchange rate and the enable checkbox are separate settings.'],[285,1570,1470,2725],[[300,1600,1460,2140],[300,2165,1460,2695]])
shot(34,'Manage staff identities and roles',['Search by staff identity before editing, resetting or disabling.','The test administrator was preserved; role-separated login testing is still outstanding.'],[285,85,1480,830],[[310,330,1460,485],[310,495,1460,750]])
shot(35,'Create a staff account with appropriate roles',['A unique username and temporary password are required.','Choose only the job roles the staff member needs. The dialog was inspected and canceled.'],[390,360,1110,1040],[[420,475,1070,650],[420,660,1070,900]])
shot(36,'Distinguish managed image quota from physical disk space',['Managed scans have their own retention and capacity policy.','Templates, PDFs, database and backups are not interchangeable with disposable scan derivatives.'],[285,90,1480,1200],[[310,300,1090,745],[310,790,1460,1180]])
shot(37,'Retention cleanup confirmation',['Only tracked images eligible under the policy are targeted.','Deletion is irreversible; scores, corrections and reports are retained by design. The cleanup was canceled in this run.'],[490,450,1015,865],[[515,500,990,645],[515,655,990,815]])
shot(38,'Inspect background processing by job and state',['Switch from action-needed to all jobs to see completed work.','Do not retry a completed or unknown-outcome job merely because a retry button exists.'],[280,90,1470,1180],[[300,285,1460,460],[300,465,1460,840]])
shot(39,'Set the test type before uploading the PDF',['Other / social studies / normal is the demonstrated configuration.','The workflow is settings, PDF, creation plan, generation; the upload appears after required choices.'],[300,80,1430,1100],[[405,295,1340,700],[405,750,1340,1040]])
shot(40,'Create a new dated reception',['Workflow Practice was entered as the session class for this test.','Starting reception creates a separate session without modifying the published criteria.'],[420,330,1090,1000],[[445,430,1060,625],[445,655,1060,950]])
shot(41,'The dashboard directs a real unresolved paper',['The next action is name confirmation for the newly processed scan.','The recent row is unassigned; the original finalized paper remains below it.'],[285,80,1480,925],[[310,230,885,530],[310,560,1460,840]])
shot(42,'Search by student number and confirm identity',['Candidate matching uses the roster; the unavailable OCR did not produce this choice.','Controlled negative scenario: Bob was selected for a fixture printed Alice. Chapter 25 corrects this; never copy that mismatch in real work.'],[285,90,1480,950],[[550,300,845,840],[875,350,1460,840]])
shot(43,'Processed scan reaches teacher review',['A low-contrast warning remains visible after preprocessing.','Provider-free handling produced review items; it was not a successful AI transcription or grade.'],[285,85,1480,1160],[[310,440,1460,530],[310,870,1460,1100]])
shot(44,'Inspect the paper before changing a score',['The complete scan is the teacher reference.','No transcription was available: the unresolved row must be reviewed rather than blindly confirmed.'],[280,250,1470,1420],[[290,450,1010,1335],[1020,605,1455,1400]])
shot(45,'Enter answer, points, outcome and reason',['Teacher-entered cats, 10 points and correct outcome are ready to save.','A reason and note describe the manual correction; saving creates an audit trail.'],[1000,555,1470,1415],[[1020,825,1455,1095],[1020,1110,1455,1385]])
shot(46,'All three questions reviewed: 20/30',['The total is two correct answers and one incorrect answer.','Every row is confirmed and the paper is ready to finalize; this is still separate from finalization.'],[285,110,1470,940],[[300,240,1455,395],[1020,420,1455,675]])
shot(47,'Finalization has explicit prerequisites',['The selected paper is Test Bob at this intermediate test stage.','Identity disposition, grading and unresolved reviews must all pass before finalization.'],[285,85,1480,850],[[310,325,885,585],[915,325,1460,765]])
shot(48,'Finalization affects progress and reports',['Read the confirmation before releasing the paper.','A later correction requires an audited reopen; this dialog is not a PDF-send action.'],[485,440,1020,800],[[510,480,990,620],[510,645,990,745]])
shot(49,'Question-level results after finalization',['Result identity and total were checked. Bob is the interim controlled mismatch, corrected later.','The kanji badge is misleading for these English questions: API says not_applicable (F02).'],[285,85,1480,1050],[[310,245,1460,365],[310,405,1460,970]])
shot(50,'Choose what enters the result PDF',['The installed dialog describes a result-only report without scans or internal staff notes.','Current-source behavior can also prepend eligible original PDFs; verify the installed version before promising a format.'],[395,385,1110,965],[[420,485,1080,810],[420,835,1080,915]])
shot(51,'Only distribute a completed PDF',['The host has verified the generated file.','This interim export is historical evidence. After a student correction, generate a new PDF as demonstrated.'],[385,370,1110,1020],[[415,500,1080,815],[415,840,1080,965]])
shot(52,'Record why a finalized result is reopened',['The previous result remains in history.','Use a concrete reason and note, then return to the grading workspace.'],[475,420,1010,890],[[500,495,985,705],[500,720,985,835]])
shot(53,'Reopening keeps scores but resets review state',['The total is still 20/30.','All three questions require confirmation again; reopening does not silently release the old score.'],[285,110,1470,940],[[300,235,1450,410],[1020,415,1450,680]])
shot(54,'Bulk confirmation is for one reviewed paper',['The checkbox attests that these three answers and scores have been checked.','The operation changes confirmation state, not points or transcription; concurrent edits should abort it.'],[480,415,1015,845],[[505,475,990,660],[505,680,990,800]])
shot(55,'Close new intake without canceling existing work',['Closing stops new uploads while existing processing continues.','It can be reopened later, unlike an archived session.'],[485,450,1010,820],[[510,490,985,660],[510,685,985,785]])
shot(56,'Closed reception keeps its finalized answer',['The status is ended and upload controls are replaced by a closed message.','The result remains available; an API upload attempt was rejected with 409.'],[285,85,1480,1080],[[310,410,1460,640],[310,805,1460,985]])
shot(57,'Archiving is a one-way session state',['All papers must be final or canceled and all processing complete.','The dialog explicitly says an archived session cannot be reopened. This test canceled the archive.'],[480,445,1010,875],[[505,510,985,695],[505,705,985,830]])
shot(58,'Change your password without exposing it',['Enter the existing password and confirm a new password of at least 12 characters.','The dialog warns that other logged-in sessions end. No credentials are displayed in this capture.'],[480,370,1020,1040],[[505,475,995,845],[505,865,995,985]])
shot(59,'Correct a finalized paper assigned to the wrong student',['Choose the student printed on the scan: Alice / RET-001.','The reason and note explain the controlled mismatch; the dialog promises score preservation and audit history.'],[465,320,1050,1070],[[490,440,1025,675],[490,685,1025,1010]])
shot(60,'Corrected identity, same 20/30 score',['The title and student number now identify Alice.','The result revision advanced to 11; regenerate reports so the current identity is used.'],[285,80,1480,690],[[310,115,1460,240],[310,255,1460,375]])
shot(61,'Two results now belong to the correct student',['The chart has two dated 66.7% points for Alice.','Use the table to verify underlying scores and dates rather than interpreting the line alone.'],[285,90,1480,1210],[[310,320,1460,445],[310,475,1460,1140]])

C=[]
def add(title,status,scenario,steps,verify,recovery,evidence,images=()):
    C.append(dict(id=len(C)+1,title=title,status=status,scenario=scenario,steps=steps,verify=verify,recovery=recovery,evidence=evidence,screens=[S[n] for n in images]))

add('Sign in and plan the teaching day','PASS - live / INSPECTED',
 'Before the first class, the duty teacher needs to know which papers require attention and which receptions still accept scans.',
 ['Open the school-provided HTTPS address and sign in with your own staff account. Resolve certificate or login errors before entering pupil information.',
  'Read Next actions on the dashboard. Work through identity, grading and finalization in that order when those items exist.',
  'Open the correct dated reception from the open-session panel. Use the recent-paper table to investigate a specific upload.',
  'Use the top-right account menu for password maintenance and sign out when leaving a shared workstation.'],
 ['The signed-in name and role match you. The host connection indicator is connected.',
  'Empty action panels mean no currently listed task of that kind; also check system health and processing status.'],
 ['If navigation counts lag a completed job, revisit the page and compare the session status. Do not upload the same paper again just to refresh a count.',
  'A lockout or certificate failure needs its own repair; repeatedly guessing credentials does not help.'],
 'Stored synthetic administrator login succeeded. Dashboard and menu were exercised. Shared-device logout and every role-specific login were not separately tested in this run.',[1,41])

add('Search and filter the student roster','PASS - live',
 'An office worker receives a parent inquiry and must identify the correct pupil among similar names and multiple classes.',
 ['Open 生徒. Search using the student number first when it is available; otherwise use name, kana or a known alias.',
  'Combine enrollment, class, course and grade filters. Inspect the displayed filter chips rather than assuming an earlier filter was cleared.',
  'Choose student-number, name or update-time sort, direction and page size. Open the row and compare identity details before editing.'],
 ['Class Workflow A returned Hana and Ken after CSV import. Mai in Workflow B was excluded.',
  'The record displays the expected student number, not merely a familiar display name.'],
 ['Clear filters if a pupil disappears. An inactive pupil is hidden by the default active-enrollment filter.',
  'A blank stored class is an actual data problem. Do not treat a class typed earlier into a form as evidence it persisted.'],
 'Live search/list navigation and class filtering passed. The roster ended with seven synthetic students. Pagination controls were inspected; a large multi-page live dataset was not seeded.',[2,24])

add('Add or edit one student - and verify the save','DEFECT F01 / PASS - identity fields',
 'A new pupil joins mid-term, so the office adds one student rather than importing the entire class.',
 ['Choose 生徒を追加. Enter a unique student number, names and kana. Fill grade, class, course and a short operational note if needed.',
  'Save once and open the resulting basic-information page. Compare every field with the form, especially class and student number.',
  'For an existing pupil, use 編集 and repeat the same post-save comparison. If another staff member changed the record, reload before retrying.'],
 ['This run created WF-104 / Demo Riku and preserved names, kana, grade, course and note.',
  'FAIL: Workflow A was entered but disappeared after save. The API also omitted class. The current frontend uses classLabel while the API contract expects schoolClass.'],
 ['Use the verified CSV path with a schoolClass column for class values until the UI mapping is corrected, then re-open the record.',
  'Do not proceed to class-specific distribution with incomplete class data. The similar edit mapping is a code-review risk, not a separately completed UI regression.'],
 'Reproduced against installed 0.9.8; same mapping remains in tested source. Eight API guardrails include duplicate identity, missing revision and stale update rejection.',[25,26,3])

add('Import a class roster from CSV','PASS - API / UI file picker blocked',
 'At the beginning of term, import several pupils and verify that existing student numbers are updated rather than duplicated.',
 ['Prepare a CSV with studentNumber, familyName, givenName, familyNameKana, givenNameKana, gradeLabel, schoolClass and course. Preserve leading zeroes in student numbers.',
  'Choose CSVから取り込む, select the file and encoding, and preview before applying. Our sample used UTF-8 with a BOM.',
  'Check create/update/skip/error counts and sample rows. Correct encoding, identity collisions or invalid rows before applying the intended strategy.',
  'After application, reload the roster and filter by the imported class. Previewing the same file again should identify existing numbers as updates.'],
 ['First preview: 3 creates, 0 updates, 0 errors. Application: 3 created, 0 updated, 0 skipped.',
  'Repeat preview: 0 creates, 3 updates, 0 errors. Class filtering confirmed two Workflow A rows and one Workflow B row.'],
 ['If the browser cannot open the file chooser, fix the extension/file-access configuration. That automation limitation is not proof that ordinary manual upload is broken.',
  'A preview is not an import. Verify the applied result and the stored records.'],
 'Wizard entry screen inspected. Multipart CSV preview and apply were tested through authenticated application APIs because the browser file chooser timed out.',[22,23])

add('Maintain aliases and spelling variants','PASS - live create / INSPECTED delete',
 'A pupil writes a romanized or alternate name on a paper. Preserve the official identity while giving name matching a useful alternate spelling.',
 ['Open the pupil and choose 別名・表記. Review existing aliases first to avoid duplicate or misleading entries.',
  'Enter an actual alternate spelling and select the appropriate type. Add it, then verify it appears on the record.',
  'When a spelling is wrong, remove only the erroneous alias using its row control. Keep official names and student numbers accurate independently.'],
 ['Riku Demo was added to Demo Riku. The API confirmed aliasType=romanized and normalizedValue=RikuDemo.',
  'An alias helps candidate matching; it does not authorize automatic assignment to a similarly named student.'],
 ['If the wrong pupil appears as a candidate, verify the number and source paper before assigning. Do not add another pupil’s name as a shortcut.',
  'Check history when an alias changes unexpectedly.'],
 'Live alias creation and persistence passed, including retention through deactivation/reactivation. Alias deletion was not executed.',[4,27])

add('End enrollment and reactivate a returning student','PASS - live',
 'A pupil leaves temporarily and later returns. The office needs them removed from active selection without losing their record.',
 ['Open 基本情報 and choose the enrollment-ending action. Read the confirmation and check the pupil number.',
  'Confirm the change, then verify the inactive badge and the inactive/all filter behavior.',
  'For a returning pupil, use the reactivation control on the existing record. Do not create a second student number for the same record.'],
 ['WF-104 changed to inactive and back to active. Its name, note and alias remained available.',
  'Enrollment status is independent from historical grades. It is not a hard-delete operation.'],
 ['If the pupil is missing from a list, change the enrollment filter before recreating them.',
  'Investigate number conflicts rather than reusing a past number for a different child.'],
 'Both live transitions succeeded. This synthetic pupil had no past results, so preservation of that pupil’s historical scores was not separately demonstrated.',[28,29,30])

add('Use progress charts in a parent meeting','PASS - live',
 'A teacher discusses progress across recent tests and needs the underlying scores, dates and limitations visible.',
 ['Open the student’s 学習推移 tab. Choose a date range or the one-, three-, six-month or all-period preset.',
  'Select a subject if the records use a consistent subject vocabulary. Read each plotted point together with the table below it.',
  'Open a result row to explain an individual answer. Distinguish changes in performance from differences in test difficulty.'],
 ['The initial record showed one 20/30 result, 66.7%, as a single point.',
  'After the practice result was correctly reassigned to Alice, two dated 20/30 results appeared in her chart and table.'],
 ['Missing results may be unfinalized, outside the date range, assigned elsewhere or filtered by a mismatched subject label.',
  'The synthetic legacy template uses English rather than the Japanese subject labels. Standardize production metadata instead of assuming those are identical filters.'],
 'One-point and two-point views were captured. Progress is a display of finalized results, not a validated causal measure of learning effect.',[5,61])

add('Review a student’s results and audit history','PASS - live viewing',
 'A teacher needs to trace a questioned score or understand a roster correction without modifying the record.',
 ['Open テスト結果 to find finalized tests for this student. Check date, title, total and score percentage.',
  'Follow the result link for question-level answers and teacher corrections. Use the source paper where still available.',
  'Open 変更履歴 to inspect recorded changes. Compare the timestamp and action with the reported issue.'],
 ['Student result navigation opened the expected retained finalized paper.',
  'History was readable and distinct from the alias and progress tabs.'],
 ['If a result belongs to another pupil, use the audited student-change workflow. Do not modify names to disguise a wrong assignment.',
  'If the score is wrong, reopen the paper with a reason rather than editing an exported PDF.'],
 'All five student-detail tabs were exercised. A complete cross-version audit-history comparison remains part of the unfinished retention upgrade test.',[6,7,14])

add('Choose, reuse and archive a template','PASS - catalogue / DOCUMENTED archive',
 'A teacher repeats a weekly test while preserving the scoring criteria used for earlier classes.',
 ['Open テストひな形 and filter by subject, grade, category, course, state or type. Inspect the version and point total.',
  'Open a published template to compare question criteria with the uploaded source. Use it to start a new reception for a new sitting.',
  'Archive a retired template from the catalogue only when it should stop appearing in normal new-session choices. Use the archived filter and restore action if it is needed again.'],
 ['The retained template has three questions and a 30-point total. Published fields are disabled.',
  'Archiving a template is documented to preserve old versions, sessions, papers, results and audit history.'],
 ['Do not edit historical criteria in place to change an old score. Use the supported new-version/draft workflow and deliberate publication.',
  'An active AI generation task can prevent archiving; resolve or finish that task before trying again.'],
 'Catalogue filters and published editor were inspected. Template archive/restore was not executed because the retained published fixture remained in use.',[8,9])

add('Create a template: HOP, STEP, placement or Other','INSPECTED settings / BLOCKED generation',
 'A teacher receives a new source PDF and must choose how its pages become templates before AI processing begins.',
 ['Choose ひな形を作成. Select test type and subject first; the PDF does not silently determine those settings.',
  'HOP creates one template per page. STEP requires a page count divisible by six and groups paired pages into three variants. Placement and Other use the whole PDF as one unit.',
  'For Other, choose normal or fill-in-the-blank format. Then select one PDF and review the proposed page grouping before starting generation.'],
 ['The live form exposed PDF upload only after required choices were complete. Other / social studies / normal was selected.',
  'Confirm page boundaries and source role before generation; the fixed creation plan should describe exactly what will be produced.'],
 ['Fix the PDF if STEP page count or page boundaries are wrong. Do not compensate by publishing a misleading partial template.',
  'If AI is unavailable, keep the source and resolve the connection; a settings screenshot is not a successful generation test.'],
 'Settings screen tested. Browser file chooser blocked and Gemini capability check unhealthy. Generation, multi-unit completion and final publication were not completed live.',[39])

add('Monitor generation and complete the final check','DOCUMENTED / PASS - automated coverage',
 'After a source PDF is accepted, a teacher must review all generated units before making them usable for pupils.',
 ['Check the generation progress page and each unit’s state. Preserve the original plan and source if a unit fails.',
  'At final check, compare generated questions and answers against the PDF. Resolve grade, naming conflicts and required metadata.',
  'Use the prescribed HOP/STEP names and variant suffixes. For editable Other titles, choose a clear school naming convention.',
  'Confirm only after blocking validation issues are resolved and teacher review is complete.'],
 ['All planned units should be accounted for; no missing or duplicated pages should be accepted silently.',
  'Generated content is a draft. The teacher, not the provider’s confidence score, is responsible for release.'],
 ['A partial generation failure is not permission to invent a completed final-check screen or publish incomplete units.',
  'Preserve the batch ID and safe error code for support. Repeated provider retries should follow job policy.'],
 'This is the source/documentation workflow. Automated generation/batch tests passed; no live successful AI generation screenshot exists in this run.')

add('Review question text, answers and point totals','INSPECTED published editor / DOCUMENTED draft editing',
 'An AI draft or teacher-authored template must reflect the actual assessment rather than plausible but invented answers.',
 ['Select each question in the left list and compare the center source sheet with the editor. Switch between blank and model-answer sources when supplied.',
  'Check label, question text, maximum points, grading method and canonical answer. Sum all points and compare them with the paper.',
  'Correct the draft and review accepted variants. Resolve every blocking issue before publication, then confirm that the published version is read-only.'],
 ['The retained three-question example is cats, cold and am, worth ten points each.',
  'A correct total is necessary but not sufficient: every question must map to the intended source and answer.'],
 ['Do not accept a plausible generated answer without checking a model answer or teacher judgment.',
  'If the published version is wrong, use a new draft/version and a controlled correction process for existing results.'],
 'Source switching and published read-only fields were inspected. No fresh draft publication through the blocked AI path was represented as passed.',[9])

add('Select grading rules for real classroom answers','DOCUMENTED / INSPECTED controls',
 'A test combines short exact answers, numeric responses, multiple choice and answers requiring teacher judgment.',
 ['Choose AI grading for judgments that need semantic interpretation, exact/registered-variant grading for controlled text, numeric grading for numbers, and choice grading for fixed options.',
  'Use teacher grading when automation is inappropriate. Define point increments and partial-credit expectations explicitly.',
  'For kanji requirements, variants, all-or-nothing groups or order-independent answers, inspect advanced settings and verify examples that should pass and fail.'],
 ['Two semantically different answers should not be merged merely by aggressive normalization.',
  'The displayed kanji badge is currently unreliable for not_applicable. Verify the underlying criterion and actual score, not the badge alone.'],
 ['Unexpected partial credit requires checking maximum points, increments, outcome and rationale together.',
  'A missing transcription must go to teacher review; this test’s provider-free fallback did exactly that.'],
 'Advanced controls inspected; scoring/rule logic covered by automated suites. Live successful AI judgment and every grading preset were not individually exercised through a new template.',[10])

add('Structure large questions and manage accepted answers','DOCUMENTED / PASS - automated coverage',
 'A multi-part paper has major sections, middle questions and optional subquestions, with more than one acceptable spelling.',
 ['Model 大問 as an optional grouping without a direct score. Use 中問 as the required scoring structure and 小問 only where needed.',
  'Avoid a direct major-to-small jump. Compare the hierarchy with the printed paper so exported result labels make sense.',
  'Register canonical and accepted alternative answers deliberately. Record provenance and teacher verification rather than treating every AI suggestion as approved.',
  'Check rubric notes, always-review flags and point increments before release.'],
 ['Point totals should count scoring questions once, not both a group and its children.',
  'Accepted variants should handle equivalent answers while preserving meaningful distinctions.'],
 ['If a generated hierarchy or answer list is ambiguous, keep the template in draft until corrected.',
  'Teacher-only rubric notes should not be copied into pupil-facing exports. Verify export content using a synthetic sample.'],
 'The current code/specification describes these rules and the automated grading/template suites passed. This run did not create a live multi-level draft from AI.')

add('Start a dated reception for the right class','PASS - live',
 'The same published assessment is given to a new class on a new date. It needs a separate intake session, not altered historical metadata.',
 ['From the published template choose 受付を開始. Check inherited test title, subject, grade, category and course.',
  'Set the date printed on the papers and the optional target class. Review the confirmation text.',
  'Start reception and verify the new session has the correct date/class and state 受付中. Communicate that specific reception to scanning staff.'],
 ['The live test created Workflow Practice on 19 September without changing the original Retention A reception.',
  'The new session initially showed zero papers; the original published template remained unchanged.'],
 ['If the wrong session is open, stop before upload. Correcting date/class after results exist requires careful review.',
  'Identically titled sessions are distinguished by date, class and session ID.'],
 'Created through the browser. A bounded roster of synthetic test students was attached through the API before uploading the practice scan.',[11,40,12])

add('Prepare scans and preserve page order','INSPECTED ordered uploader / PASS - API preprocessing',
 'A scan operator has a stack of multi-page answer sheets from one class.',
 ['Confirm how many pages form one answer. Scan each pupil’s complete paper consecutively, starting with the page containing their name.',
  'For the current ordered-upload interface, prepare one-page PDF files. Use filenames whose natural numeric order matches the physical stack.',
  'Review order, blank pages, missing pages, rotation and page quality before confirming upload. Keep one pupil’s later pages beside their first page.'],
 ['Page type checks cannot prove ownership of pages without names. A plausible page-two image from another pupil can still be wrong.',
  'The practice PNG was processed through the underlying API, not the ordered one-page-PDF browser picker.'],
 ['If grouping is wrong, fix the source/order before release; do not rely on an AI score to detect a swapped later page.',
  'A low-contrast warning warrants visual inspection or a rescan. A technically accepted file is not necessarily readable.'],
 'Ordered-upload UI inspected. Legacy/API PNG upload and preprocessing succeeded with a low-contrast warning. Multi-page scanner ingestion was not completed live.',[13])

add('Monitor uploaded papers and processing failures','PASS - live/API',
 'The operator has uploaded a paper and needs to know whether it is still processing, needs a teacher, or has failed.',
 ['Open the session and compare upload, needs-review and finalized counts. Search for the filename or pupil.',
  'Filter by AI/image issues, identity review, grading review, ready to finalize, finalized or failed.',
  'Open the paper’s available action. Use the dashboard or review queue to continue the corresponding task.'],
 ['The practice upload moved from preprocessing to needs_name_review, then teacher grading after assignment.',
  'Provider-free grading created three unresolved questions with no transcription. This is a recoverable manual path, not AI success.'],
 ['Do not re-upload because a row has not refreshed instantly. Inspect the job and timestamp first.',
  'For a true failure, preserve the submission/job identifiers and safe error rather than copying credentials or raw provider payloads into support notes.'],
 'Upload finalization, local raster preprocessing and live queue transitions were observed. The session’s page column displayed a dash despite the detail reporting one page; it was not used as the sole page-count check.',[43])

add('Resolve a pupil name using the original paper','PASS - manual UI / AI name reading blocked',
 'A name is unreadable or ambiguous, so a teacher must match the paper to the roster.',
 ['Open the 生徒名 review tab. Inspect the first-page image and any candidate information or quality warning.',
  'Search by the printed student number, then compare name, grade and class. Select the unique matching candidate.',
  'Assign only after identity is clear. If it remains unreadable or is not a pupil answer, use the appropriate unresolved/non-student disposition rather than guessing.'],
 ['A successfully assigned paper leaves the name queue and can proceed to grading.',
  'The UI can accept a wrong manual choice. In this controlled test Bob was assigned to a scan printed Alice, then corrected using the audited result workflow.'],
 ['Never treat the test’s intentional mismatch as recommended practice. A teacher must verify the printed identity.',
  'If a finalized paper was assigned incorrectly, change the student with a reason and regenerate the report; do not edit the PDF name manually.'],
 'Manual search/selection/assignment succeeded. Name OCR was unavailable. The negative assignment and its recovery are documented in chapters 25 and 26.',[42,16])

add('Handle unreadable names and duplicate attempts','DOCUMENTED / PASS - automated coverage',
 'A scan may be unnamed, a non-student page, a second attempt, or a duplicate of an already received paper.',
 ['Compare the original paper and upload history before deciding. Distinguish a legitimate retake from an accidental duplicate scan.',
  'For unreadable identity use the supported unidentified state; for a non-student document use the corresponding disposition.',
  'When duplicate assignment requires a decision, choose the intended attempt/canonical-paper treatment and record why. Keep earlier audit history.'],
 ['A canonical representative result should be deliberate; duplicate uploads must not silently become multiple reports to the same guardian.',
  'Unidentified results can prevent export selection from being safely resolved.'],
 ['If the UI cannot distinguish attempts confidently, stop and ask the responsible teacher to identify the intended result.',
  'Do not delete database files or paper objects to make a duplicate disappear.'],
 'Duplicate and identity rules are covered in source tests. The live practice path used one new paper; unidentified/non-student and same-session duplicate resolution were not executed.')

add('Grade a paper while viewing the original scan','PASS - live teacher workflow',
 'AI transcription is unavailable, but a teacher can still read the scan and record an accountable score.',
 ['Open the paper’s grading workspace. Confirm student, test date, version and page count before editing.',
  'Select each question and use the full scan or available crop as evidence. Enter the observed answer, points and outcome.',
  'Choose a reason, add a useful note, and save/confirm. The workspace advances through unresolved questions.',
  'Compare the final total with a manual sum and confirm no question remains unresolved.'],
 ['The example was entered as cats=10, cold=10, is=0, totaling 20/30.',
  'Changing transcription, score and outcome is a teacher override with history; it does not retroactively make the missing OCR successful.'],
 ['Do not bulk-confirm blank or unreadable defaults when the scan shows an answer.',
  'Use the full scan if per-question crops are absent. An absent crop is different from an expired or missing original scan.'],
 'All three answers were manually saved through the UI. Correct/incorrect outcomes and reason/note capture were exercised. Partial-credit scoring was not separately performed live.',[44,45,46])

add('Use the grading queue without confusing its counts','PASS - live workspace / INSPECTED queue',
 'Several papers need review, but the number of question decisions differs from the number of pupils.',
 ['Use 採点待ち・確認 to separate name, grading and finalization work. Read whether a count represents papers or question items.',
  'Open the relevant paper and inspect each unresolved result. Confirm only after reading the source and criteria.',
  'After saving, revisit the session and queue to confirm progression. Allow the background state update to arrive.'],
 ['The practice paper generated three question reviews for one pupil, then became ready to finalize after all three were confirmed.',
  'The empty grading and finalization screens were captured before the practice workflow and are not proof of completed grading by themselves.'],
 ['A navigation badge may briefly lag the current page. Compare authoritative result state and refresh navigation before retrying a mutation.',
  'If unresolved items persist, check the reason code and job state rather than repeatedly clicking save.'],
 'The full per-paper review path was executed. Empty queue states were inspected; every queue keyboard shortcut was not tested.',[17,18])

add('Bulk-confirm answers that have already been checked','PASS - live',
 'A finalized paper was reopened for an administrative check, and its answers and scores have all been verified unchanged.',
 ['Review every question in the current paper first. Click the 未確認...問を一括確認 action.',
  'Read that this applies only to the displayed pupil’s paper and does not change scores or transcription.',
  'Tick the acknowledgment and confirm. Reload instead of forcing the operation if another teacher changed an item.'],
 ['After reopen, all three questions returned to review while preserving 20/30.',
  'Bulk confirmation restored confirmed state and the paper was re-finalized without changing its total.'],
 ['Do not use this to bypass missing OCR review. A zero or unreadable default can be wrong even if the button is available.',
  'Concurrent edits require re-reading current data; stale acceptance must not silently overwrite another teacher.'],
 'Live bulk acknowledgment and confirmation passed. Concurrent-edit rejection was covered in automated tests, not simulated with two live browser accounts.',[53,54])

add('Finalize only after identity and grading checks','PASS - live',
 'The teacher has checked all answers and wants the result to appear in progress and report selection.',
 ['Open the 確定 tab and choose the intended paper. Compare pupil, date, title and total.',
  'Check that identity/disposition, grading availability and resolved-review requirements all pass.',
  'Click この答案を確定, read the confirmation, then finalize. Verify the result appears in 帳票 with the expected score.'],
 ['The live paper appeared with 20/30 and 66.7% after confirmation.',
  'Finalization is distinct from creating a PDF or delivering a message. It may create downstream candidates only when newer configured automation is active.'],
 ['If the paper is absent from the ready queue, resolve the failed prerequisite first.',
  'If an error is found after release, reopen with a reason, review again and re-finalize.'],
 'UI finalization and report appearance passed. Re-finalization after the reopen/bulk-confirm exercise used the authenticated API and advanced the result revision.',[47,48])

add('Reopen a finalized paper to correct a score','PASS - live reopen and review',
 'A teacher notices a transcription or marking mistake after results have been finalized.',
 ['Open the finalized result and choose 採点を修正. Select a concrete reason and record what needs rechecking.',
  'Open the grading workspace. Existing scores remain, but the paper’s questions return to review.',
  'Make any justified correction, reconfirm the questions and finalize again. Generate a fresh export for the revised result.'],
 ['In the no-score-change recovery test, 20/30 survived reopen and reconfirmation.',
  'The result revision advanced. Earlier exports are historical artifacts and should not be mistaken for the current result.'],
 ['Do not alter a downloaded PDF to hide a correction; that loses the app’s audit trail.',
  'If the error is only the pupil identity, use the separate student-change operation, which preserves scores.'],
 'Reopen dialog, note, review reset and bulk reconfirmation were exercised. The original retention baseline paper was not reopened.',[52,15])

add('Correct a finalized paper assigned to the wrong pupil','PASS - live',
 'The teacher discovers that a scan printed Alice / RET-001 was assigned to Bob / RET-002 during intake.',
 ['Open the result and choose 生徒を変更. Inspect the original paper again to establish the correct number and name.',
  'Search/select Alice / RET-001, choose the mismatch reason and record the correction in a note.',
  'Confirm the change. Check the new heading and student number, unchanged score and advanced result revision.',
  'Regenerate the PDF and recheck both pupils’ result lists and the intended pupil’s progress chart.'],
 ['The controlled Bob assignment changed to Alice. The total stayed 20/30 and result revision became 11.',
  'Alice’s progress showed the original 18 September result plus the corrected 19 September practice result.'],
 ['An older Bob PDF remains unsuitable for distribution even if its numeric score is correct.',
  'If the target already has an attempt in that same session, resolve the duplicate/canonical-attempt policy before proceeding.'],
 'Reassignment completed in the browser. Revised PDF text contains Alice and not Bob. Same-session duplicate reassignment was not part of this live case.',[59,60])

add('Create, inspect and deliver an individual PDF','PASS - live generation / API download',
 'A teacher needs a pupil-facing report after confirming the latest score and identity.',
 ['Open the finalized result, check the pupil/date/revision, then choose 結果PDF.',
  'Read the output-content confirmation and request generation. Wait for the verified/download-ready state.',
  'Download and open the PDF. Check Japanese text, pupil number, total, question answers and absence of internal notes.',
  'After any correction, generate a new PDF. Distribute only the approved current file through the school’s intended channel.'],
 ['The installed 0.9.8 dialog describes a result-only export without scans. Current-source documentation also describes prepending eligible original PDFs; do not assume both versions have identical output.',
  'The corrected practice PDF parsed successfully, contains Alice, and excludes Bob.'],
 ['If generation is pending or failed, inspect its job rather than sending an incomplete file.',
  'Missing scan pages may reflect original format or retention. Check the declared output rules before treating a result-only PDF as a defect.'],
 'Created through UI; downloaded using the returned application file API and parsed. School Manager upload/receipt was not executed.',[49,50,51])

add('Export a class set of finalized result PDFs','PASS - live/API small sample',
 'At the end of a test, the office wants a ZIP containing each selected pupil’s official result PDF.',
 ['In 帳票, apply class/date/template filters or select specific rows. Distinguish checked rows from all matching filtered results.',
  'Start the appropriate bulk export. Review the host-calculated pupil and result counts and acknowledge the scope.',
  'Wait for verified completion, download the ZIP, and inspect manifest.csv and the pupil folders.',
  'Confirm the PDF count, IDs, dates and totals against the intended selection before distributing individual files.'],
 ['The live export processed one pupil/one result, showed 1/1 complete, and produced a CRC-valid ZIP with a parseable PDF and manifest.',
  'This ZIP is a collection of official result PDFs. It is not a new cumulative class-ranking report.'],
 ['Changed/reopened/reassigned results can invalidate the selection snapshot. Re-preview when the job reports stale or superseded.',
  'Documented limits are 100 pupils, 500 results and a verified ZIP of 512 MiB. Split an oversized request; do not assume the one-result live test covers those limits.'],
 'Small live job and structural ZIP validation passed. Boundary limits, invalid selections and stale jobs were covered by automated tests.',[19,20,21])

add('Print the teacher-facing class pass/fail matrix','UNAVAILABLE installed / DOCUMENTED newer source',
 'A teacher needs one internal table showing pupils against tests, with a configurable passing threshold.',
 ['On a version that includes 合否表, apply the desired class, dates and other report filters first.',
  'Review the default 60% threshold and set the school’s intended pass mark. Compare totals and cells with underlying finalized results.',
  'Choose 合否表をPDF出力 and use the browser’s Save as PDF print destination. Check pagination and all columns before handing it to staff.'],
 ['The teacher matrix is independent of a ZIP export and independent of School Manager’s anonymized recipient-specific class report.',
  'The current endpoint bounds a response at 2,000 pupils/20,000 results. A printed table does not change finalized scores.'],
 ['If the endpoint returns HTML, the installed feature is absent; an HTTP 200 is not proof of a matrix response.',
  'Do not send an internal named class table to every guardian. Use the separate anonymized workflow and verify its output.'],
 'Installed 0.9.8 returned text/html from /api/v1/reports/pass-fail-matrix. Current source/tests exist, but the live matrix and print dialog were unavailable. No fake screenshot is supplied.')

add('Close and reopen reception at the end of the day','PASS - close / INSPECTED reopen',
 'Scanning has finished, but teachers may still need to complete review and finalization.',
 ['Open the session and choose 受付を終了. Confirm that this stops new uploads but lets already submitted processing continue.',
  'Verify the ended state and the closed-intake message. Continue reviewing existing papers if needed.',
  'For a late arrival, use 受付を再開 before uploading. Recheck that it is the same intended dated session.'],
 ['The practice session closed while preserving its finalized answer.',
  'A direct attempt to create a new upload against the closed session returned 409.'],
 ['Closing reception is different from canceling jobs and different from archiving.',
  'Do not create another identically named session solely because the existing one is closed; determine whether reopening is appropriate.'],
 'Live closure and API enforcement passed. Reopen control was inspected but not executed; the final practice session was left closed.',[55,56])

add('Archive a completed session safely','INSPECTED / DOCUMENTED irreversible state',
 'A completed term’s session should leave normal operational lists while remaining available for reference.',
 ['Close the reception first. Check that all papers are finalized or canceled and no upload, duplicate review, ordered intake or grading job remains active.',
  'Choose アーカイブ and read the prerequisite list and one-way-state warning.',
  'Only confirm after the session is genuinely complete. Use the archived filter later to view its preserved history.'],
 ['The dialog states that an archived session is read-only and cannot reopen reception.',
  'Papers, results and corrections are not deleted by this state transition.'],
 ['If prerequisites fail, finish or resolve the outstanding work instead of forcing a database update.',
  'Template archiving has a restore workflow; session archiving has different rules. Do not treat the two as interchangeable.'],
 'The real archive confirmation was captured and canceled. No archival mutation or restore claim is made.',[57])

add('Configure Gemini 3.8 Flash and verify capability','INSPECTED / LIVE AI BLOCKED',
 'The administrator prepares AI before teachers depend on template extraction, name reading or grading.',
 ['Open 管理 > AI設定. Read the actual model ID and use gemini-3.8-flash as requested for this installation.',
  'Use the key-add/exchange control for a new key. Enter it only in the protected form and never in screenshots or logs.',
  'Run the connection/capability check once and inspect its timestamp, image-input and structured-output results.',
  'Confirm all four feature profiles become usable, then run a small synthetic acceptance paper before real classroom use.'],
 ['The installed screen shows gemini-3.8-flash but reports gemini_request_invalid and unusable profiles.',
  'An old friendly connection label mentioning another model is not the authoritative model setting.'],
 ['Do not repeatedly replace a valid key to compensate for an incompatible thinking payload or unavailable model.',
  'Resolve the installed version and probe outcome first. If a credential is lost after migration, re-enter it through the protected setup path.'],
 'Existing encrypted key was preserved. Current source thinking fix passed tests; successful live model capability was not established.',[32])

add('Understand all four AI features and thinking settings','PASS - automated fix / LIVE FEATURES BLOCKED',
 'The administrator needs to distinguish a healthy connection from proof that every AI-assisted teaching workflow works.',
 ['Template extraction reads the source and produces a draft. Validate questions, answers, hierarchy and metadata before publication.',
  'Name reading examines the first page and supports roster matching. Ambiguous identity must still be resolved by a teacher.',
  'Initial grading combines the paper and criteria. Review uncertain transcriptions, rubric decisions and partial credit.',
  'Adjudication rechecks flagged judgments. A second AI judgment is still subject to teacher review and finalization.'],
 ['The fixed source does not use MINIMAL for Gemini Direct 3.7/3.8 family requests. Probes and non-template profiles use LOW; extraction uses MEDIUM.',
  'Regression cases include family/version variants and case handling. These are source behavior, not an installed-user thinking dropdown.'],
 ['If the model rejects the thinking level, update to the fixed build and verify actual request success.',
  'If the provider reports model unavailable, resolve account/model access rather than declaring the source fix a live acceptance pass.'],
 'Ten new regression cases were part of the earlier committed fix and passed in the fresh suite. All four successful live AI workflows remain blocked.')

add('Handle multi-page AI work and failed retries','DOCUMENTED / PASS - automated coverage',
 'A long answer sheet needs controlled AI processing without losing question coverage or accidentally duplicating cost.',
 ['Verify source page order and page count before processing. The current source chunks long papers deterministically and preserves page order.',
  'Monitor the job and affected paper rather than launching a new independent request for each delay.',
  'After processing, compare expected questions with returned results, including missing, duplicated or unreadable answers.',
  'Use teacher review or the documented retry path only after understanding the safe error and current job state.'],
 ['The source documents up to 32 images per chunk and additional byte/payload limits; multi-page correctness depends on all chunks being reconciled.',
  'A completed transport request is not proof every question was graded correctly.'],
 ['429 or transient provider errors need quota/budget review and the defined retry policy. Persistent schema/model errors need configuration repair.',
  'Do not replay an unknown external send as if it were an AI request retry; School Manager has stricter unknown-outcome handling.'],
 'Automated provider/chunk and job logic passed. No live 3-50-page AI paper, provider outage or network interruption was executed.')

add('Set AI pricing, budgets and usage monitoring','INSPECTED / DOCUMENTED save behavior',
 'An administrator wants predictable spending and evidence of failures or slow AI processing.',
 ['Expand 利用量・費用の詳細設定. Inspect request totals, estimated cost and operation metrics for the displayed period.',
  'Register a price snapshot for the exact connection using the provider’s official pricing source. Enter input/output/thinking units correctly.',
  'Set daily and monthly warnings and hard limits, then choose whether the budget guard is enabled. Check the USD/JPY conversion separately.',
  'After a small successful request, verify the usage changes and confirm the intended limit behavior.'],
 ['The inspected system had zero recorded requests and no price snapshot for the connection.',
  'The UI warns that an enabled budget guard can stop processing when prices are missing. A zero usage counter does not prove a live request succeeded for free.'],
 ['Do not double-count thinking tokens if the provider already includes them in output billing.',
  'This guide intentionally provides no unverified current price or exchange-rate recommendation. Confirm the live provider price when configuring.'],
 'Controls and warnings were inspected; prices/budgets were not mutated and paid limit-crossing tests were not performed.',[33])

add('Configure optional OpenRouter without confusing models','DOCUMENTED / NOT LIVE TESTED',
 'A school chooses an optional alternate provider while retaining a clear record of which model powers image-based tasks.',
 ['Expand OpenRouter in AI settings only if the school intends to use it. Add the connection and protected credential using the supported form.',
  'Verify that the chosen model supports both image input and structured output. Text-only capability is insufficient for scanned-paper tasks.',
  'Probe the connection, inspect active profiles and run an anonymous sample through every assigned feature.',
  'Track the returned actual cost and compare it with the intended provider/model record.'],
 ['A connection label alone is not enough. Check the exact provider and model used by each profile.',
  'Switching provider can change answer behavior; repeat template and grading acceptance rather than assuming identical results.'],
 ['If the provider lacks vision or structured output, choose a suitable model through the supported configuration rather than bypassing the capability check.',
  'Keep working prior credentials/profiles until a replacement check succeeds.'],
 'No OpenRouter credential was supplied and its live paths were not exercised. Relevant opt-in provider tests were among the skipped cases; this chapter is operational guidance only.')

add('Create staff accounts and assign roles','INSPECTED / PASS - automated authorization',
 'A school adds a teacher, scanner operator or office viewer without sharing the administrator login.',
 ['Open 管理 > 職員アカウント and choose 職員を追加. Enter a unique username, display name and temporary password of at least 12 characters.',
  'Choose roles appropriate to the job: administrator, teacher, scan operator or results viewer. Avoid giving every user administrative access.',
  'Have the new user complete the initial password change within the stated 24-hour setup window.',
  'For a role change, edit the account and verify the user’s actual allowed screens and operations from a separate login.'],
 ['A viewer should see permitted finalized information; an uploader should operate only permitted receptions; a teacher handles roster, grading and reports.',
  'The user list displays role, enabled state and last login.'],
 ['For departure or long inactivity, disable instead of deleting. Disabling invalidates active sessions; re-enabling does not restore them.',
  'The last active administrator is protected. Establish an alternate administrator before retiring one.'],
 'Staff list and create-role dialog inspected. No new staff account or separate-role browser login was created in this run; authorization behavior has automated coverage.',[34,35])

add('Change, reset or retire a staff credential','INSPECTED / DOCUMENTED',
 'A staff member changes their password, forgets it, or leaves the school.',
 ['For self-service, open the account menu and パスワードを変更. Enter current password, new password and confirmation.',
  'For an administrator reset, find the exact staff account and use パスワード再設定. Deliver the temporary credential through an appropriate direct channel.',
  'For departure, disable the account and verify current sessions are invalidated. Re-enable only when access is again intended.'],
 ['The self-service dialog warns that other sessions end after a password change.',
  'The operations guide distinguishes a 24-hour initial account setup window from a 30-minute reset completion window.'],
 ['A successful reset request is not proof the user completed their mandatory change; verify the resulting account state.',
  'Do not include passwords, auth cookies or API keys in a report or support capture.'],
 'Self-service dialog inspected and canceled to preserve the retained administrator credential. Password mutation, expiry and session revocation were not live-tested.',[58])

add('Read health checks and use maintenance mode','INSPECTED / PASS - read API',
 'Before a busy scanning day or an update, an administrator checks whether the host can safely accept work.',
 ['Open 管理 > システム状態. Read database/schema, file storage, disk, workers, certificate, AI and backup items separately.',
  'Investigate every warning that affects the planned work. Connected navigation and an HTTP-ready endpoint are only parts of readiness.',
  'Use maintenance mode for an approved service operation, coordinate ongoing work, and verify both entry and exit as part of that operation.'],
 ['The installed host’s basic data/files/workers/certificate checks were healthy, but AI and backup items were not ready.',
  'The screenshot’s warning state accurately reflects those unresolved dependencies.'],
 ['Do not dismiss a backup warning because ordinary grading works. An update/restore has different prerequisites.',
  'If maintenance remains enabled after repair, verify operation markers and the supported completion procedure before clearing anything.'],
 'Health page and JSON were inspected. Maintenance mode was not toggled during this walkthrough; the attempted update never completed elevation.',[31])

add('Manage scan retention and physical disk capacity','INSPECTED / CLEANUP NOT EXECUTED',
 'The host is accumulating scans, and the administrator must distinguish normal retention cleanup from loss of grades or backups.',
 ['Open 保存容量. Compare the managed-image usage with its policy limit and separately read overall physical disk free space.',
  'Review the oldest scan, next cleanup time, previous deletion count and category breakdown.',
  'If running policy cleanup, read the confirmation carefully. It targets tracked eligible scan images in age order, not arbitrary selected files.',
  'After a real cleanup, verify that scores, answer text, correction history and reports remain while only eligible scans are unavailable.'],
 ['The inspected policy described daily cleanup after three months and capacity thresholds. The displayed decimal GB and binary GiB units may differ.',
  'Templates, reports, database and backups are separate categories; managed-image cleanup is not a full backup policy.'],
 ['Do not delete or rename DataRoot files in Explorer to free space. The app must maintain storage records and relationships.',
  'A report surviving an unopened cleanup dialog is not evidence that retention deletion works.'],
 'Storage view and irreversible cleanup confirmation were inspected. Cleanup was canceled; time-expiry, quota-pressure and post-deletion access were not live-tested.',[36,37])

add('Investigate background jobs before retrying','PASS - live viewing / DOCUMENTED retries',
 'A teacher reports that an upload, AI operation or PDF export appears stuck.',
 ['Open 処理状況 and start with action-needed items. Switch to all jobs to see completed work and compare timing.',
  'Record job type, ID, state, attempt count, next time and safe error. Correlate it with the paper/export the teacher sees.',
  'Repair the actual cause before using a retry action. Preserve the existing operation history rather than creating repeated duplicates.'],
 ['The run showed completed preprocessing, provider-free grading, individual PDF and bulk-export jobs.',
  'A completed job can still have a retry button; availability is not a recommendation to replay it.'],
 ['Provider quota errors, missing files and stale report snapshots require different remedies.',
  'An external send with an unknown outcome must be reconciled in School Manager, not blindly retried from a generic job page.'],
 'Job filters and completion states inspected. Failed-job retry, cancellation, outages and restarts were not injected live.',[38])

add('School Manager: individual-result delivery','UNAVAILABLE installed / DOCUMENTED newer source',
 'After a teacher finalizes a paper, a configured newer host can prepare a guardian-only message with its verified report.',
 ['Install a build that includes School Manager integration and configure the dedicated account using the supported technician script.',
  'Begin in dry-run mode, then create new synthetic finalized results after activation. Pre-existing results are not automatically back-sent.',
  'Match a unique student by exact number and name and verify guardian-only selection.',
  'For a separately authorized dummy live test, inspect the uploaded report and confirm the dummy guardian actually receives the intended revision.'],
 ['The integration must use the current verified PDF and correct recipient; finalization alone is not evidence of delivery.',
  'Dry run validates login, matching and progression to compose; it does not upload a file or send the message.'],
 ['recipient_not_found, recipient_ambiguous and guardian_only_selection_invalid require roster/registration repair.',
  'Do not send to a real guardian to demonstrate a feature. Use a dedicated agreed dummy recipient.'],
 'No live delivery occurred. The installed API is absent and no dedicated School Manager login/recipient was available. Automated tests use a simulated external client.')

add('School Manager: send a whole-class report privately','DOCUMENTED privacy contract / NOT LIVE DELIVERED',
 'Each guardian needs to see their child’s result in the context of classmates, without receiving classmates’ identities.',
 ['Prepare a stable .xls or .xlsx with a supported student-ID column, class column and unambiguous grade information.',
  'After activation, place the new file in DataRoot/school-manager/incoming. The watcher should create recipient-specific reports.',
  'For each recipient, verify only rows from the same grade AND class remain. Remove all ID/name/kana columns from the output.',
  'Inspect the recipient row and anonymized peers before any live send. Cross-class or cross-grade rows must not appear.'],
 ['Current policy labels the recipient 本人 and peers 同級生01, 同級生02, etc. Classmates are NOT shown as their student numbers.',
  'The user’s earlier student-number preference has not been implemented. This privacy behavior must not be silently described as that requested behavior.'],
 ['Reject ambiguous IDs/classes/grades and arbitrary free-text cells. Supported outcomes are constrained numeric scores and pass/fail symbols/statuses.',
  'Limits documented in source: 20 MiB, 500 columns and 10,000 rows. Check duplicates and dangerous extra identity columns, not only the obvious name column.'],
 'Source privacy tests passed. No real School Manager class-report upload or receipt was verified. The teacher-facing named matrix is a different feature.')

add('School Manager: configure and inspect a dry run','DOCUMENTED / PREREQUISITES MISSING',
 'A technician prepares an integration acceptance test without sending notifications.',
 ['Run Set-OokiGraderSchoolManager.ps1 with the Ooki Grader URL, administrator username, dedicated School Manager username and -Enable. Enter passwords in the secure prompts.',
  'Do not add -LiveSending for initial setup. Confirm dryRun=true in the authenticated JSON status response.',
  'Inspect /api/v1/admin/school-manager/ and deliveries?limit=200 for configuration, timestamps, safe errors and delivery states.',
  'Create fresh synthetic post-activation inputs and verify exact unique recipient matching and guardian-only compose progression.'],
 ['A real JSON response is required. The installed 0.9.8 returned the SPA HTML shell at both URLs, so no dry-run settings were actually read.',
  'A validated dry run is still not an attachment or delivery test.'],
 ['If configuration is unavailable, complete the version update first. Do not infer enabled=false from missing properties in an HTML response.',
  'Keep passwords out of command arguments, evidence and screenshots. Record only safe state and identifiers.'],
 'The relevant API was checked for status AND content type. This corrects any earlier inference from a bare HTTP 200. No dry-run login was performed.')

add('School Manager: live sending and daily limits','DOCUMENTED / NOT LIVE TESTED',
 'The school moves from a verified dummy dry run to controlled live delivery and needs predictable handling of updates and uncertain sends.',
 ['Before enabling -LiveSending, record the pending delivery list. Existing dry-run-validated candidates can become eligible for actual send.',
  'For class reports, check the Tokyo calendar day. Each pupil has a one-per-day cap; later same-day changes are coalesced for a later opportunity.',
  'Verify recipient, attachment and received message in the dummy account, not merely the host’s queued state.',
  'When disabling, use the supported script without enable/live switches and inspect cancellation of unsent candidates.'],
 ['A send whose response is unknown stops with message_send_outcome_unknown and does not automatically retry.',
  'For class reports, unknown outcome consumes that day’s slot to avoid duplicate sends.'],
 ['Inspect School Manager’s sent messages before any manual retry. Never infer failure simply because the host lost the response.',
  'Reactivation establishes a new activation boundary; older results should not be retroactively swept into delivery.'],
 'One-per-day, activation and failure handling have automated coverage. Real clock-boundary operation, upload, sending and receipt remain unverified.')

add('Update the installed application through its updater','BLOCKED elevation / DOCUMENTED procedure',
 'A technician installs a new build while preserving the school’s populated database, reports and credentials.',
 ['Read the update documentation matching the package. Verify package checksums/signature or the explicitly supported on-site package policy.',
  'Coordinate the maintenance window, stop new work, verify health, enter maintenance and create/fully verify a fresh pre-update backup.',
  'Use the supported updater/Upgrade-OokiGrader path with actual installed version, data root and verified backup evidence. Do not replace executable files manually.',
  'After update, verify the running version and health before comparing the seeded data and reopening classroom operations.'],
 ['The updater stages a new version while preserving the old version directory. A failed schema-changing update must not simply start an older binary against the new schema.',
  'A successful empty-database upgrade from earlier testing does not establish retention of populated school data.'],
 ['The current elevated launch was canceled by Windows/UAC. No successful result marker or updated installed version exists.',
  'Automatic approval review rejected both attempted separate fixed-instance launches with only “blocked by policy”; no alternative launch was attempted afterward.'],
 'Fixed 0.9.15-local.1 package and a checkpoint script exist, but the populated-data upgrade did not complete. Screenshots in this guide are honestly labeled installed 0.9.8.')

add('Prove data retention before and after an update','BASELINE VERIFIED / UPDATE COMPARISON INCOMPLETE',
 'A real retention test must contain meaningful pre-update records, not just an empty installation that still opens.',
 ['Before update, record student IDs/numbers, aliases, enrollment, class/course, template versions, source files, sessions and roster membership.',
  'Include a processed paper with teacher corrections, a finalized score, history and a verified PDF. Record hashes for immutable source/report objects.',
  'After the updater and service restart, compare IDs, counts, relationships, score totals, object hashes and report readability.',
  'Test encrypted credential usability and active AI profiles, then create one new post-update workflow. Keep old and new snapshots clearly dated.'],
 ['The original retained result remains 20/30 and its downloaded PDF SHA-256 exactly matches the pre-update fixture.',
  'This proves the current baseline survived our additional testing. It does NOT prove migration retention, because the populated update is still unfinished.'],
 ['If counts differ, inspect expected migrations and audit evidence before restoring. A hash mismatch requires explanation, not just an “opens successfully” claim.',
  'Never point an older binary at a newer incompatible schema to test retention. Restore a matching cold snapshot through the supported recovery process.'],
 'Meaningful baseline exists and was checked again. Cross-version retention, rollback after a failed migration and post-update key usability remain open gates.')

add('Create and fully verify backups','UNCONFIGURED installed / DOCUMENTED',
 'The school needs a recoverable copy independent of normal scan retention and the host’s primary disk.',
 ['Configure the backup destination through the supported installation/technician process; it is not an arbitrary folder picker in the teacher UI.',
  'Check encryption state and the destination’s availability. Start a manual backup only when configuration is valid.',
  'Wait for complete verification, inspect its manifest/hash and run the read-only restore-plan check.',
  'Keep school-approved retention, off-device protection and periodic restore drills. Measure recovery objectives with actual results.'],
 ['A backup “created” status is weaker than fully verified. A restore-plan check is weaker than an actual restore.',
  'Backing up only report PDFs does not preserve the database, aliases, audit relationships or protected configuration.'],
 ['Do not treat a copied live SQLite file as a validated cold backup.',
  'Resolve unavailable/encryption-unconfirmed status before relying on the backup for an upgrade.'],
 'Installed health shows backup configuration/readiness issues. No successful new full backup or restore plan was claimed in this run.')

add('Restore, repair and uninstall without losing data','DOCUMENTED / NOT EXECUTED THIS RUN',
 'A technician recovers a failed host or repairs application files while preserving the school data boundary.',
 ['For restore, verify the backup and restore plan, explicitly stop the service, and use the documented offline Restore-OokiGrader workflow.',
  'Validate schema, object inventory and offline health. Preserve rollback snapshots until acceptance is complete.',
  'Use Repair-OokiGrader for the documented repair case rather than mixing arbitrary versions. Recheck checksums and health afterward.',
  'For uninstall, confirm what happens to the application and DataRoot using the matching package instructions; do not equate uninstall with data deletion.'],
 ['The operations guide says restore can leave the service stopped, maintenance enabled and operation markers intact.',
  'A supported, verified restoration-completion runbook is required; marker deletion is not a generic fix.'],
 ['Do not start old binaries against migrated data. Do not remove restore/migration markers without understanding the operation state.',
  'DPAPI-protected keys may require re-entry or rewrapping on a different Windows host.'],
 'Current test suite includes tool/packaging tests. Full clean-Windows install/reboot/update/failure-rollback/restore/uninstall acceptance remains outstanding.')

add('Install, trust TLS and connect other school devices','DOCUMENTED / EXISTING HOST ACCESS VERIFIED',
 'A technician brings a host on site and teachers access it from other authorized school computers.',
 ['Follow the on-site installation guide for the package and verify hardware, storage, service, data root and network prerequisites.',
  'Configure the expected school hostname and TLS certificate. Establish trust on peer devices using the supported trust package.',
  'Use the intended HTTPS origin consistently. Check DNS, certificate name/validity and access from a separate school device.',
  'Complete administrator bootstrap, staff setup, AI acceptance and backup readiness before handing the system to teachers.'],
 ['This walkthrough used the functioning local HTTPS origin ooki-grader.test. Existing certificate/host checks were healthy.',
  'A localhost browser success does not establish peer-device DNS, firewall, TLS trust or school-network availability.'],
 ['Do not bypass a certificate warning as a permanent deployment step. Fix the hostname/trust configuration.',
  '403 responses can involve origin/CSRF protection rather than a broken password. Diagnose the correct layer.'],
 'Existing host sign-in and same-origin APIs worked. New installation, separate-device access, reboot and firewall changes were not executed.')

add('Daily, weekly and failure-response checklist','DOCUMENTED HANDOFF / VERIFIED COMPONENTS NOTED',
 'The school needs a repeatable routine and a short decision path when a teacher reports a problem.',
 ['Daily: inspect health, open receptions, unresolved names/grades, failed jobs and disk/backup alerts. Confirm the intended template/date/class before intake.',
  'After marking: reconcile pupil count, scan count, finalized results and exports. Check the latest revision after any identity or score correction.',
  'Weekly: review staff access, backup verification, restore-plan readiness, AI usage and unresolved delivery errors.',
  'Monthly or after an update: run the synthetic acceptance workflow, inspect role boundaries and exercise the scheduled isolated restore drill.'],
 ['For a missing pupil, check filters and enrollment; for a missing report, check assignment/finalization/export state; for AI failure, check exact model/profile/probe.',
  'For a whole-class report, verify grade/class isolation and anonymity independently from transport and receipt.'],
 ['Capture the time, screen, safe code and relevant IDs. Do not capture API keys or passwords.',
  'Stop repeated submissions when the outcome is uncertain. Resolve the actual state before retrying, especially for School Manager messages.'],
 'Use the coverage matrix and evidence labels to decide what still needs a real acceptance run. This guide deliberately does not certify all features as passing.')

# Additional live checks completed during final acceptance review.
shot(62,'A real session-expiry warning',[
 'The UI warned that the session was about to expire; save work before the deadline.',
 'The staff fetch also failed at this point. Extension was attempted but did not establish a renewed session; the app returned to login.'],[260,40,1480,710],[[275,80,1470,200],[300,350,1460,630]])
shot(63,'Expired sessions return to staff login',[
 'The application now requires a fresh staff login.',
 'The password field is empty in the capture; no stored secret is displayed.'],[440,250,1060,1020],[[470,360,1030,625],[470,645,1030,890]])
shot(64,'Login rate limiting is visible to staff',[
 'Repeated role-login testing reached the rate limit. Respect the cooldown instead of rapid retries.',
 'A rate-limit message is different from proof that the password is wrong.'],[420,230,1080,1040],[[450,350,1050,585],[450,600,1050,915]])
C[0]['evidence']='Administrator login succeeded initially. Later, an actual expiry warning and return to login were observed. Separate API tests verified logout and revoked-session denial. Login throttling was documented during repeated role-account testing.'
C[0]['screens'] += [S[62],S[63],S[64]]
C[8]['status']='PASS - live/API catalogue and archive lifecycle'
C[8]['evidence']='Published catalogue/editor inspected. A separate unused synthetic template was created through the API, archived, restored to draft with the same ID, and re-archived. The original retained template was preserved.'
C[28]['status']='PASS - live close / API reopen and re-close'
C[28]['evidence']='Browser closure and API upload rejection passed. The practice reception was then reopened through the API and returned to closed, preserving its result.'
C[35]['status']='PASS - live API roles / INSPECTED UI forms'
C[35]['evidence']='A separate synthetic account was created, completed password setup, and exercised viewer, scanOperator and teacher permissions in fresh authenticated API sessions. Allowed reads and denied mutations/admin access matched expectations. The account was disabled afterward.'
C[36]['status']='PASS - live API password and session lifecycle'
C[36]['evidence']='Live checks passed for initial password change, admin reset, disable/enable, old-session invalidation, fresh login, reset requiring another change, logout and access denial. No administrator password was changed. Time-based expiry windows were not accelerated.'
C[37]['status']='PASS - live health / API maintenance'
C[37]['evidence']='Health page/JSON inspected. Maintenance was entered briefly; an ordinary write returned 503; maintenance was exited in a finally block. The service was left available. This is not a completed updater test.'
Path(__file__).with_name('workflow_guide_content.json').write_text(json.dumps(C,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'chapters':len(C),'screenPages':sum(len(c['screens']) for c in C),'uniqueScreensUsed':len(set(s['file'] for c in C for s in c['screens']))}))
