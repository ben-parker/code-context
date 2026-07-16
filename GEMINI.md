## Planning Behavior
Any time we begin work on a feature, bug fix, refactor, or any change to the code, we will discuss and agree together on a plan to implement the change.  After my initial statement of the requirements, present me with your plan to get it done. As part of your planning process, fully investigate the files affected by this change to ensure you have the full context of what needs to be done, including any changes needed to unit tests that may cover this section of code, dependencies, other code that calls the method, etc.

I may have additional questions in my response to your initial plan. You will address all my clarifying questions before we start work. We will go back and forth until I don't have any additional questions. Then, making sure to update your plan and account for our discussion and my questions, we can begin working on the change.

## Tracking work
As part of creating a plan, use a todo list internally to hold yourself accountable and stay on task. Once we have agreed ona plan and we begin the work, your first step should always be transforming the plan into a detailed task list. This tasklist should be stored in a markdown file, named based on the work we are tracking, in the project directory so that we can keep track of progress across conversations. Any time a feature or task is completed, make sure to update the markdown file and mark the task as complete, or whatever the latest status is.

## Development Style
Make sure you write code using a TDD approach:
1. write a unit or integration test for the feature.
2. Run the single test or tests to ensure it fails.
3. Implement the feature
4. Run the single test or tests to ensure they pass.
5. Run all tests to ensure no regressions are introduced.

Use xUnit as the testing framework and NSubstitute as the mocking library as needed.

Domain models should be rich and expose methods to mutate their internal state, but avoid putting business logic in model classes. Service classes will orchestrate state changes and handle business logic.

In general, use SOLID principles and industry best practices.

Refer back to @codecontext-prd.md any time you need to understand the full context of what we are building, or how a feature might fit in to the larger application or vision.

VERY IMPORTANT: Before making any changes, you must get the user's explicit approval to continue with your plan.  The first step of the plan should always be to update the todo list you created with all items unchecked. As you complete each item, update the list and mark them complete.
